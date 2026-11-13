#include "BlockMesher.hpp"

#include "Converter/Entity/Blueprint.hpp"
#include "Converter/Entity/Block.hpp"
#include "Converter/Entity/Body.hpp"
#include "Converter/ConvertSettings.hpp"

#include "ObjectDatabase/MaterialManager.hpp"
#include "ObjectDatabase/ModelStorage.hpp"
#include "ObjectDatabase/ProgCounter.hpp"
#include "ObjectDatabase/ObjectData.hpp"

#include "Utils/Console.hpp"
#include "Utils/String.hpp"

#include <unordered_map>
#include <unordered_set>
#include <algorithm>
#include <limits>
#include <tuple>
#include <map>

SM_UNMANAGED_CODE

//
// SM MERGED BLOCKS
//

SMMergedBlocks::SMMergedBlocks(
	const BlockData* pParent,
	const SMColor color,
	const glm::mat4& preTransform
)
	: SMEntity()
	, m_parent(pParent)
	, m_color(color)
	, m_model(std::make_unique<Model>())
{
	m_preTransform = preTransform;
}

SMMergedBlocks::~SMMergedBlocks() = default;

EntityType SMMergedBlocks::Type() const
{
	return EntityType::MergedBlocks;
}

const SMUuid& SMMergedBlocks::GetUuid() const
{
	return m_parent->m_uuid;
}

SMColor SMMergedBlocks::GetColor() const
{
	return m_color;
}

// Must produce exactly the same material names as SMBlock, so the materials and the Blender plugin keep working
char* SMMergedBlocks::GetMtlNameCStr(
	const std::string_view& material,
	const std::size_t idx,
	char* pCString) const
{
	pCString = m_parent->m_uuid.toCString(pCString);
	*pCString++ = ' ';
	pCString = m_color.StringHexCStr(pCString);
	*pCString++ = ' ';
	pCString = String::FromInteger<std::size_t>(idx + 1, pCString);
	*pCString++ = ' ';

	return MaterialManager::GetMaterialACStr(m_parent->m_textures.m_material, pCString);
}

std::string SMMergedBlocks::GetMtlName(const std::size_t idx) const
{
	std::string v_mtlName(m_parent->m_uuid.toString());
	v_mtlName.append(1, ' ');
	m_color.appendStringHex(v_mtlName);
	v_mtlName.append(1, ' ');
	String::AppendIntegerToString(v_mtlName, idx + 1);
	MaterialManager::AppendMaterialIdx(v_mtlName, m_parent->m_textures.m_material);

	return v_mtlName;
}

void SMMergedBlocks::FillTextureMap(EntityTextureMap& textureMap) const
{
	std::string v_mtlName = this->GetMtlName(0);
	if (textureMap.find(v_mtlName) != textureMap.end())
		return;

	textureMap.emplace(
		std::move(v_mtlName),
		ObjectTexDataConstructInfo(m_parent->m_textures, m_color)
	);
}

void SMMergedBlocks::WriteObjectToFile(
	std::ofstream& file,
	WriterOffsetData& offset,
	const glm::mat4& transform) const
{
	// Vertices are already in body space
	m_model->WriteToFile(transform * m_preTransform, offset, file, this);

	ProgCounter::ProgressValue++;
}

glm::mat4 SMMergedBlocks::GetTransformMatrix() const
{
	return glm::mat4(1.0f);
}

Model& SMMergedBlocks::GetModel() noexcept
{
	return *m_model;
}

//
// BLOCK MESHER
//

namespace
{
	struct IVec3Hasher
	{
		inline std::size_t operator()(const glm::ivec3& v) const noexcept
		{
			std::size_t h = static_cast<std::size_t>(static_cast<std::uint32_t>(v.x)) * 73856093ull;
			h ^= static_cast<std::size_t>(static_cast<std::uint32_t>(v.y)) * 19349663ull;
			h ^= static_cast<std::size_t>(static_cast<std::uint32_t>(v.z)) * 83492791ull;
			return h;
		}
	};

	struct IVec2Hasher
	{
		inline std::size_t operator()(const glm::ivec2& v) const noexcept
		{
			return (static_cast<std::size_t>(static_cast<std::uint32_t>(v.x)) << 32)
				| static_cast<std::size_t>(static_cast<std::uint32_t>(v.y));
		}
	};

	struct BlockRef
	{
		SMBlock* block;
		std::vector<SMEntity*>* container;
	};

	struct Cell
	{
		const BlockData* parent;
		std::uint32_t group;
	};

	// Direction index: axis = dir >> 1, positive = dir & 1
	// -X, +X, -Y, +Y, -Z, +Z
	struct FaceGroup
	{
		std::vector<SMEntity*>* container;
		const BlockData* parent;
		SMColor color;
		glm::mat4 preTransform;
		std::size_t index; // child index of a source block: tells which physical body the merged mesh belongs to

		// (dir, plane coordinate) -> 2D cells on that plane
		std::map<std::pair<int, int>, std::vector<glm::ivec2>> slices;
	};

	struct Rect
	{
		glm::ivec2 origin;
		glm::ivec2 size;
	};

	/*
		Same projection as GenerateUVs() in Block.cpp, expressed per face direction.
		Block.cpp evaluates it in the unrotated block frame; here it is evaluated in body space,
		which is identical for blocks with the default rotation and seamless for all blocks.
	*/
	inline glm::vec2 BlockUv(const int axis, const bool positive, const glm::vec3& p)
	{
		switch (axis)
		{
		case 0:  return positive ? glm::vec2(p.y, -p.z) : glm::vec2(p.y, p.z);
		case 1:  return positive ? glm::vec2(-p.x, -p.z) : glm::vec2(p.x, -p.z);
		default: return glm::vec2(p.x, p.y);
		}
	}

	// Greedy rectangle decomposition of a set of 2D cells
	void GreedyMerge(std::vector<glm::ivec2>& cells, std::vector<Rect>& outRects)
	{
		std::sort(cells.begin(), cells.end(),
			[](const glm::ivec2& a, const glm::ivec2& b) {
				return (a.y != b.y) ? (a.y < b.y) : (a.x < b.x);
			});

		std::unordered_set<glm::ivec2, IVec2Hasher> v_remaining(cells.begin(), cells.end());

		for (const glm::ivec2& v_cell : cells)
		{
			if (!v_remaining.contains(v_cell))
				continue;

			int v_width = 1;
			while (v_remaining.contains(glm::ivec2(v_cell.x + v_width, v_cell.y)))
				v_width++;

			int v_height = 1;
			for (;;)
			{
				bool v_rowFull = true;
				for (int a = 0; a < v_width; a++)
				{
					if (!v_remaining.contains(glm::ivec2(v_cell.x + a, v_cell.y + v_height)))
					{
						v_rowFull = false;
						break;
					}
				}

				if (!v_rowFull) break;
				v_height++;
			}

			for (int y = 0; y < v_height; y++)
				for (int x = 0; x < v_width; x++)
					v_remaining.erase(glm::ivec2(v_cell.x + x, v_cell.y + y));

			outRects.push_back({ v_cell, glm::ivec2(v_width, v_height) });
		}
	}

	void EmitQuad(
		Model& model,
		SubMeshData& subMesh,
		std::unordered_map<glm::ivec3, std::size_t, IVec3Hasher>& vertexMap,
		const int dir,
		const int plane,
		const Rect& rect,
		const int tiling)
	{
		const int v_axis = dir >> 1;
		const bool v_positive = (dir & 1) != 0;
		const int v_axisU = (v_axis + 1) % 3;
		const int v_axisV = (v_axis + 2) % 3;

		// Counter clockwise around +axis, (axisU x axisV == axis)
		glm::ivec2 v_corners[4] =
		{
			{ rect.origin.x               , rect.origin.y                },
			{ rect.origin.x + rect.size.x , rect.origin.y                },
			{ rect.origin.x + rect.size.x , rect.origin.y + rect.size.y  },
			{ rect.origin.x               , rect.origin.y + rect.size.y  }
		};

		if (!v_positive)
			std::swap(v_corners[1], v_corners[3]);

		const float v_tilingAdj = 1.0f / static_cast<float>(std::max(tiling, 1));

		std::vector<VertexData> v_face;
		v_face.reserve(4);

		for (const glm::ivec2& v_corner : v_corners)
		{
			glm::ivec3 v_pos;
			v_pos[v_axis] = plane;
			v_pos[v_axisU] = v_corner.x;
			v_pos[v_axisV] = v_corner.y;

			std::size_t v_vertIdx;
			const auto v_iter = vertexMap.find(v_pos);
			if (v_iter != vertexMap.end())
			{
				v_vertIdx = v_iter->second;
			}
			else
			{
				v_vertIdx = model.m_vertices.size();
				model.m_vertices.emplace_back(v_pos);
				vertexMap.emplace(v_pos, v_vertIdx);
			}

			std::size_t v_uvIdx = 0;
			if (SharedConverterSettings::ExportUvs)
			{
				v_uvIdx = model.m_uvs.size();
				model.m_uvs.push_back(BlockUv(v_axis, v_positive, glm::vec3(v_pos)) * v_tilingAdj);
			}

			v_face.emplace_back(v_vertIdx, v_uvIdx, static_cast<std::size_t>(dir));
		}

		subMesh.m_dataIdx.push_back(std::move(v_face));
	}

	bool IsTransparentBlock(
		const BlockData* pParent,
		std::unordered_map<const BlockData*, bool>& cache)
	{
		const auto v_iter = cache.find(pParent);
		if (v_iter != cache.end())
			return v_iter->second;

		const std::size_t v_matIdx = MaterialManager::GetMaterialIdx(pParent->m_textures.m_material);
		// 2 - Glass, 5 - DifAsgNorAlpha (see Resources/MaterialIds.json)
		const bool v_transparent = (v_matIdx == 2 || v_matIdx == 5);

		cache.emplace(pParent, v_transparent);
		return v_transparent;
	}
}

BlockMesher::Stats BlockMesher::Optimize(SMBlueprint* pBlueprint, const int mode)
{
	Stats v_stats;
	if (mode == BlockOpt_None || pBlueprint == nullptr)
		return v_stats;

	// 1. Gather all the blocks together with the container they live in
	std::vector<BlockRef> v_blocks;

	const auto v_gather = [&v_blocks](std::vector<SMEntity*>& container) -> void {
		for (SMEntity* v_pEntity : container)
			if (v_pEntity->Type() == EntityType::Block)
				v_blocks.push_back({ static_cast<SMBlock*>(v_pEntity), &container });
	};

	v_gather(pBlueprint->m_objects);
	for (SMEntity* v_pEntity : pBlueprint->m_objects)
		if (v_pEntity->Type() == EntityType::Body)
			v_gather(static_cast<SMBody*>(v_pEntity)->m_objects);

	if (v_blocks.empty())
		return v_stats;

	v_stats.blocksIn = v_blocks.size();
	v_stats.cubeFacesIn = v_blocks.size() * 6;

	// 2. Split the blocks by physical body (faces are never culled between bodies, since bodies can move)
	std::map<std::size_t, std::vector<const BlockRef*>> v_bodyBlocks;
	for (const BlockRef& v_ref : v_blocks)
	{
		const auto v_iter = pBlueprint->m_childToBodyIndex.find(v_ref.block->m_index);
		const std::size_t v_bodyIdx = (v_iter != pBlueprint->m_childToBodyIndex.end())
			? v_iter->second
			: std::numeric_limits<std::size_t>::max();

		v_bodyBlocks[v_bodyIdx].push_back(&v_ref);
	}

	std::unordered_map<const BlockData*, bool> v_transparencyCache;
	std::vector<std::pair<std::vector<SMEntity*>*, SMMergedBlocks*>> v_newEntities;

	for (const auto& [v_bodyIdx, v_bodyRefs] : v_bodyBlocks)
	{
		std::unordered_map<glm::ivec3, Cell, IVec3Hasher> v_cells;
		std::vector<FaceGroup> v_groups;
		std::map<std::tuple<std::vector<SMEntity*>*, const BlockData*, unsigned int>, std::uint32_t> v_groupMap;

		// 3. Rasterize every block into unit cells (body space)
		for (const BlockRef* v_pRef : v_bodyRefs)
		{
			const SMBlock* v_pBlock = v_pRef->block;

			const auto v_groupKey = std::make_tuple(v_pRef->container, v_pBlock->m_parent, v_pBlock->m_color.m_color);
			auto v_groupIter = v_groupMap.find(v_groupKey);
			if (v_groupIter == v_groupMap.end())
			{
				v_groupIter = v_groupMap.emplace(v_groupKey, static_cast<std::uint32_t>(v_groups.size())).first;
				v_groups.push_back({ v_pRef->container, v_pBlock->m_parent, v_pBlock->m_color, v_pBlock->GetPreTransform(), v_pBlock->m_index, {} });
			}

			const glm::mat4 v_transform = v_pBlock->GetTransformMatrix();
			const glm::vec3 v_half = v_pBlock->GetSize() / 2.0f;

			glm::vec3 v_min(std::numeric_limits<float>::max());
			glm::vec3 v_max(std::numeric_limits<float>::lowest());

			for (int a = 0; a < 8; a++)
			{
				const glm::vec3 v_corner(
					(a & 1) ? v_half.x : -v_half.x,
					(a & 2) ? v_half.y : -v_half.y,
					(a & 4) ? v_half.z : -v_half.z
				);

				const glm::vec3 v_world = glm::vec3(v_transform * glm::vec4(v_corner, 1.0f));
				v_min = glm::min(v_min, v_world);
				v_max = glm::max(v_max, v_world);
			}

			const glm::ivec3 v_iMin(glm::round(v_min));
			const glm::ivec3 v_iMax(glm::round(v_max));

			const Cell v_cell{ v_pBlock->m_parent, v_groupIter->second };
			for (int x = v_iMin.x; x < v_iMax.x; x++)
				for (int y = v_iMin.y; y < v_iMax.y; y++)
					for (int z = v_iMin.z; z < v_iMax.z; z++)
						v_cells.try_emplace(glm::ivec3(x, y, z), v_cell);
		}

		// 4. Collect the visible faces
		for (const auto& [v_pos, v_cell] : v_cells)
		{
			for (int v_dir = 0; v_dir < 6; v_dir++)
			{
				const int v_axis = v_dir >> 1;
				const bool v_positive = (v_dir & 1) != 0;

				glm::ivec3 v_neighbourPos = v_pos;
				v_neighbourPos[v_axis] += v_positive ? 1 : -1;

				const auto v_neighbour = v_cells.find(v_neighbourPos);
				if (v_neighbour != v_cells.end())
				{
					// Same block type always hides the face, otherwise only an opaque neighbour does
					if (v_neighbour->second.parent == v_cell.parent
						|| !IsTransparentBlock(v_neighbour->second.parent, v_transparencyCache))
					{
						continue;
					}
				}

				const int v_plane = v_positive ? (v_pos[v_axis] + 1) : v_pos[v_axis];
				const glm::ivec2 v_cell2d(v_pos[(v_axis + 1) % 3], v_pos[(v_axis + 2) % 3]);

				v_groups[v_cell.group].slices[{ v_dir, v_plane }].push_back(v_cell2d);
			}
		}

		// 5. Build one mesh per group
		std::vector<Rect> v_rects;
		for (FaceGroup& v_group : v_groups)
		{
			if (v_group.slices.empty())
				continue;

			SMMergedBlocks* v_pMerged = new SMMergedBlocks(v_group.parent, v_group.color, v_group.preTransform);
			v_pMerged->m_index = v_group.index;
			Model& v_model = v_pMerged->GetModel();

			if (SharedConverterSettings::ExportNormals)
			{
				// Indexed by the direction index
				v_model.m_normals =
				{
					{ -1.0f,  0.0f,  0.0f }, { 1.0f, 0.0f, 0.0f },
					{  0.0f, -1.0f,  0.0f }, { 0.0f, 1.0f, 0.0f },
					{  0.0f,  0.0f, -1.0f }, { 0.0f, 0.0f, 1.0f }
				};
			}

			v_model.m_subMeshData.emplace_back(0, SharedConverterSettings::ExportNormals, SharedConverterSettings::ExportUvs);
			SubMeshData& v_subMesh = v_model.m_subMeshData.back();

			std::unordered_map<glm::ivec3, std::size_t, IVec3Hasher> v_vertexMap;

			for (auto& [v_sliceKey, v_sliceCells] : v_group.slices)
			{
				v_rects.clear();
				GreedyMerge(v_sliceCells, v_rects);

				for (const Rect& v_rect : v_rects)
					EmitQuad(v_model, v_subMesh, v_vertexMap, v_sliceKey.first, v_sliceKey.second, v_rect, v_group.parent->m_tiling);

				v_stats.quadsOut += v_rects.size();
			}

			v_newEntities.emplace_back(v_group.container, v_pMerged);
		}
	}

	// 6. Replace the original blocks
	std::unordered_set<SMEntity*> v_toRemove;
	std::unordered_set<std::vector<SMEntity*>*> v_containers;
	for (const BlockRef& v_ref : v_blocks)
	{
		v_toRemove.insert(v_ref.block);
		v_containers.insert(v_ref.container);
	}

	for (std::vector<SMEntity*>* v_pContainer : v_containers)
	{
		std::erase_if(*v_pContainer, [&v_toRemove](SMEntity* pEntity) {
			if (!v_toRemove.contains(pEntity))
				return false;

			delete pEntity;
			return true;
		});
	}

	for (auto& [v_pContainer, v_pMerged] : v_newEntities)
		v_pContainer->push_back(v_pMerged);

	v_stats.entitiesOut = v_newEntities.size();

	DebugOutL("[BlockMesher] blocks: ", v_stats.blocksIn,
		", cube faces before: ", v_stats.cubeFacesIn,
		", quads after: ", v_stats.quadsOut,
		", merged objects: ", v_stats.entitiesOut);

	return v_stats;
}

std::string_view SMMergedBlocks::GetPhysicsMaterial() const
{
	return m_parent->m_physicsMaterial;
}

SM_MANAGED_CODE
