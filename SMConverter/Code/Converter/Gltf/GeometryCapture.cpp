#include "GeometryCapture.hpp"

#include "ObjectDatabase/ModelStorage.hpp"

#include "Utils/String.hpp"

#include <cctype>

SM_UNMANAGED_CODE

bool CapturedMaterial::IsTransparent() const noexcept
{
	switch (shader)
	{
	case 2:  // Glass
	case 3:  // Grass
	case 4:  // Leaves
	case 5:  // DifAsgNorAlpha
	case 6:  // Water
	case 7:  // LightCone
	case 8:  // Liquid
	case 9:  // Chemicals
	case 10: // Oil
	case 11: // Clutter
		return true;
	default:
		return false;
	}
}

void GeometryCapture::Begin(CapturedMesh* pTarget, const SMEntity::EntityTextureMap* pTextureMap)
{
	s_target = pTarget;
	s_textureMap = pTextureMap;
}

void GeometryCapture::End()
{
	s_target = nullptr;
	s_textureMap = nullptr;
}

bool GeometryCapture::IsActive() noexcept
{
	return s_target != nullptr;
}

void GeometryCapture::Reset()
{
	End();
	Materials.clear();
	Shapes.clear();
	s_materialMap.clear();
	s_shapeMap.clear();
}

std::string GeometryCapture::ClassifySurface(const SMTextureList& textures, const std::size_t shader, const std::string_view& physicsMaterial)
{
	const auto v_lower = [](std::string_view s) {
		std::string r(s);
		for (char& c : r) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
		return r;
	};

	// See-through shaders keep their own look
	switch (shader)
	{
	case 2: case 6: case 8: case 9: case 10: return "glass";
	default: break;
	}

	// The render material says how the game shades it ("..._Metal" + metalProfile)
	const std::string v_render = v_lower(textures.m_material);
	const std::string v_profile = v_lower(textures.m_metalProfile);
	if (v_render.find("_metal") != std::string::npos)
		return (v_profile.find("reflection") != std::string::npos) ? "metal_bare" : "metal_painted";

	const std::string v_phys = v_lower(physicsMaterial);
	if (v_phys == "rubber" || v_phys == "tape" || v_phys == "fabric" || v_phys == "bubblewrap") return "rubber";
	if (v_phys == "wood" || v_phys == "cardboard") return "wood";
	if (v_phys == "plastic" || v_phys == "electronics") return "plastic";
	if (v_phys == "rock" || v_phys == "sand" || v_phys == "dirt" || v_phys == "ice" || v_phys == "concrete") return "stone";
	if (v_phys == "metal" || v_phys == "scrapmetal" || v_phys == "mechanical" || v_phys == "matrixium" || v_phys == "disconite")
		return "metal_painted";
	if (v_phys == "glass") return "glass";

	// Unknown (mod parts without data): keep the plain SM look
	return "standard";
}

std::uint32_t GeometryCapture::GetMaterialIndex(const SMTextureList& textures, const std::size_t shader, const std::string& surface)
{
	std::wstring v_key = textures.m_dif;
	v_key.append(1, L'|').append(textures.m_asg);
	v_key.append(1, L'|').append(textures.m_nor);
	v_key.append(1, L'|').append(std::to_wstring(shader));
	v_key.append(1, L'|').append(surface.begin(), surface.end());

	const auto v_iter = s_materialMap.find(v_key);
	if (v_iter != s_materialMap.end())
		return v_iter->second;

	const std::uint32_t v_idx = static_cast<std::uint32_t>(Materials.size());
	Materials.push_back({ textures.m_dif, textures.m_asg, textures.m_nor, shader, surface });
	s_materialMap.emplace(std::move(v_key), v_idx);

	return v_idx;
}

std::uint32_t GeometryCapture::GetShapeIndex(const SMEntity* pEntity)
{
	const auto v_iter = s_shapeMap.find(pEntity);
	if (v_iter != s_shapeMap.end())
		return v_iter->second;

	const std::uint32_t v_idx = static_cast<std::uint32_t>(Shapes.size());
	Shapes.push_back({ pEntity->GetUuid(), pEntity->GetColor(), pEntity->Type(), pEntity->GetIndex() });
	s_shapeMap.emplace(pEntity, v_idx);

	return v_idx;
}

// Material names end with " m<shader index>"
static std::size_t ParseShaderIndex(const std::string_view& mtlName)
{
	const std::size_t v_pos = mtlName.rfind(" m");
	if (v_pos == std::string_view::npos)
		return 1;

	std::size_t v_result = 0;
	for (std::size_t a = v_pos + 2; a < mtlName.size(); a++)
	{
		const char v_char = mtlName[a];
		if (v_char < '0' || v_char > '9') break;
		v_result = v_result * 10 + static_cast<std::size_t>(v_char - '0');
	}

	return (v_result == 0) ? 1 : v_result;
}

void GeometryCapture::AddModel(const Model& model, const glm::mat4& modelMatrix, const SMEntity* pEntity)
{
	if (!s_target || !pEntity) return;

	const glm::mat3 v_normalMatrix = glm::transpose(glm::inverse(glm::mat3(modelMatrix)));
	const std::uint32_t v_shape = GeometryCapture::GetShapeIndex(pEntity);

	char v_nameBuffer[512];

	const std::size_t v_subMeshCount = model.m_subMeshData.size();
	for (std::size_t mIdx = 0; mIdx < v_subMeshCount; mIdx++)
	{
		const SubMeshData& v_subMesh = model.m_subMeshData[mIdx];
		if (!pEntity->GetCanWrite(v_subMesh.m_materialName, mIdx))
			continue;

		// The material name is the link between the entity and its textures (same as the obj/mtl exporter)
		char* v_nameEnd = pEntity->GetMtlNameCStr(v_subMesh.m_materialName, mIdx, v_nameBuffer);
		const std::string_view v_mtlName(v_nameBuffer, static_cast<std::size_t>(v_nameEnd - v_nameBuffer));

		std::uint32_t v_material;
		SMColor v_color = pEntity->GetColor();

		const auto v_texIter = s_textureMap ? s_textureMap->find(v_mtlName) : SMEntity::EntityTextureMap::const_iterator();
		if (s_textureMap && v_texIter != s_textureMap->end())
		{
			const std::size_t v_shader = ParseShaderIndex(v_mtlName);
			const SMTextureList& v_textures = v_texIter->second.m_textures;
			v_material = GeometryCapture::GetMaterialIndex(v_textures, v_shader,
				GeometryCapture::ClassifySurface(v_textures, v_shader, pEntity->GetPhysicsMaterial()));
			v_color = v_texIter->second.m_texColor;
		}
		else
		{
			const std::size_t v_shader = ParseShaderIndex(v_mtlName);
			v_material = GeometryCapture::GetMaterialIndex(SMTextureList(), v_shader,
				GeometryCapture::ClassifySurface(SMTextureList(), v_shader, pEntity->GetPhysicsMaterial()));
		}

		for (const std::vector<VertexData>& v_face : v_subMesh.m_dataIdx)
		{
			if (v_face.size() < 3) continue;

			CapturedFace v_outFace;
			v_outFace.firstCorner = static_cast<std::uint32_t>(s_target->corners.size());
			v_outFace.cornerCount = static_cast<std::uint32_t>(v_face.size());
			v_outFace.material = v_material;
			v_outFace.shape = v_shape;
			v_outFace.color = v_color;

			for (const VertexData& v_vert : v_face)
			{
				CapturedCorner v_corner;
				v_corner.position = glm::vec3(modelMatrix * glm::vec4(model.m_vertices[v_vert.m_vert], 1.0f));

				if (v_subMesh.m_hasNormals && v_vert.m_norm < model.m_normals.size())
				{
					const glm::vec3 v_n = v_normalMatrix * model.m_normals[v_vert.m_norm];
					const float v_len = glm::length(v_n);
					v_corner.normal = (v_len > 1e-12f) ? (v_n / v_len) : glm::vec3(0.0f, 0.0f, 1.0f);
				}
				else
				{
					v_corner.normal = glm::vec3(0.0f);
				}

				v_corner.uv = (v_subMesh.m_hasUvs && v_vert.m_uv < model.m_uvs.size())
					? model.m_uvs[v_vert.m_uv]
					: glm::vec2(0.0f);

				s_target->corners.push_back(v_corner);
			}

			s_target->faces.push_back(v_outFace);
		}
	}
}

SM_MANAGED_CODE
