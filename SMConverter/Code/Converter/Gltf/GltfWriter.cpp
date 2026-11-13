#include "GltfWriter.hpp"

#include "Converter/ConvertSettings.hpp"
#include "Converter/Gltf/TextureOptimizer.hpp"
#include "BuildInfo.hpp"

#include "Utils/String.hpp"
#include "Utils/GlmUnmanaged.hpp"

#include <nlohmann/json.hpp>
#include <FreeImage.h>

#include <unordered_map>
#include <execution>
#include <algorithm>
#include <numeric>
#include <map>
#include <fstream>
#include <cstring>
#include <cmath>

SM_UNMANAGED_CODE

namespace
{
	using json = nlohmann::json;

	constexpr int GL_FLOAT = 5126;
	constexpr int GL_UNSIGNED_INT = 5125;
	constexpr int GL_ARRAY_BUFFER = 34962;
	constexpr int GL_ELEMENT_ARRAY_BUFFER = 34963;

	struct GlbBuilder
	{
		std::vector<std::uint8_t> bin;
		json bufferViews = json::array();
		json accessors = json::array();

		void Align4()
		{
			while (bin.size() % 4 != 0) bin.push_back(0);
		}

		int AddBufferView(const void* pData, const std::size_t size, const int target)
		{
			Align4();
			const std::size_t v_offset = bin.size();
			bin.resize(v_offset + size);
			std::memcpy(bin.data() + v_offset, pData, size);

			json v_view = { { "buffer", 0 }, { "byteOffset", v_offset }, { "byteLength", size } };
			if (target != 0) v_view["target"] = target;

			bufferViews.push_back(std::move(v_view));
			return static_cast<int>(bufferViews.size() - 1);
		}

		template<typename T>
		int AddAccessor(const std::vector<T>& data, const int componentType, const char* type, const int target, const bool withMinMax = false)
		{
			const int v_view = AddBufferView(data.data(), data.size() * sizeof(T), target);

			const std::size_t v_components = sizeof(T) / 4;
			json v_acc = {
				{ "bufferView", v_view },
				{ "componentType", componentType },
				{ "count", data.size() },
				{ "type", type }
			};

			if (withMinMax && !data.empty())
			{
				const float* p = reinterpret_cast<const float*>(data.data());
				std::vector<float> v_min(v_components, std::numeric_limits<float>::max());
				std::vector<float> v_max(v_components, std::numeric_limits<float>::lowest());
				for (std::size_t a = 0; a < data.size(); a++)
					for (std::size_t c = 0; c < v_components; c++)
					{
						v_min[c] = std::min(v_min[c], p[a * v_components + c]);
						v_max[c] = std::max(v_max[c], p[a * v_components + c]);
					}
				v_acc["min"] = v_min;
				v_acc["max"] = v_max;
			}

			accessors.push_back(std::move(v_acc));
			return static_cast<int>(accessors.size() - 1);
		}
	};

	inline glm::vec3 ToGltf(const glm::vec3& v)
	{
		return glm::vec3(v.x, v.z, -v.y);
	}

	inline float SrgbToLinear(const float c)
	{
		return (c <= 0.04045f) ? (c / 12.92f) : std::pow((c + 0.055f) / 1.055f, 2.4f);
	}

	std::wstring FileStem(const std::wstring& path)
	{
		const std::size_t v_slash = path.find_last_of(L"/\\");
		std::wstring v_name = (v_slash == std::wstring::npos) ? path : path.substr(v_slash + 1);
		const std::size_t v_dot = v_name.find_last_of(L'.');
		if (v_dot != std::wstring::npos) v_name.resize(v_dot);
		return v_name;
	}

	struct VertexKey
	{
		glm::vec3 pos, nor;
		glm::vec2 uv;
		std::uint32_t color, shape;

		bool operator==(const VertexKey& o) const noexcept
		{
			return pos == o.pos && nor == o.nor && uv == o.uv && color == o.color && shape == o.shape;
		}
	};

	struct VertexKeyHasher
	{
		std::size_t operator()(const VertexKey& k) const noexcept
		{
			std::size_t h = 1469598103934665603ull;
			const auto mix = [&h](const void* p, const std::size_t n) {
				const std::uint8_t* b = static_cast<const std::uint8_t*>(p);
				for (std::size_t a = 0; a < n; a++) { h ^= b[a]; h *= 1099511628211ull; }
			};
			mix(&k.pos, sizeof(k.pos)); mix(&k.nor, sizeof(k.nor)); mix(&k.uv, sizeof(k.uv));
			mix(&k.color, sizeof(k.color)); mix(&k.shape, sizeof(k.shape));
			return h;
		}
	};

	const char* ShaderName(const std::size_t shader)
	{
		static const char* v_names[] = { "Unknown", "DifAsgNor", "Glass", "Grass", "Leaves", "DifAsgNorAlpha",
			"Water", "LightCone", "Liquid", "Chemicals", "Oil", "Clutter" };
		return (shader < std::size(v_names)) ? v_names[shader] : "Unknown";
	}
}

bool GltfWriter::Write(
	const std::wstring& path,
	const std::string& sceneName,
	const std::vector<CapturedMesh>& meshes,
	const std::string& rootExtrasJson,
	std::string& outError)
{
	GlbBuilder v_glb;
	json v_root;
	v_root["asset"] = { { "version", "2.0" }, { "generator", "SMConverter (SMC-AD) build " + std::to_string(SM_CONVERTER_BUILD_VERSION) } };

	// ---- Which materials are actually used (faces can all be culled away)
	const std::size_t v_matCount = GeometryCapture::Materials.size();
	std::vector<std::uint8_t> v_used(v_matCount, 0);
	for (const CapturedMesh& v_mesh : meshes)
		for (const CapturedFace& f : v_mesh.faces)
			if (f.material < v_matCount) v_used[f.material] = 1;

	// ---- Optimise every texture file of the used materials once (lossless, in parallel)
	std::vector<std::wstring> v_paths;
	{
		std::unordered_map<std::wstring, int> v_seen;
		for (std::size_t m = 0; m < v_matCount; m++)
		{
			if (!v_used[m]) continue;
			const CapturedMaterial& mat = GeometryCapture::Materials[m];
			for (const std::wstring* p : { &mat.dif, &mat.asg, &mat.nor })
				if (!p->empty() && v_seen.emplace(*p, 0).second)
					v_paths.push_back(*p);
		}
	}
	std::vector<OptimizedTexture> v_optimized(v_paths.size());
	{
		std::vector<std::size_t> v_idx(v_paths.size());
		std::iota(v_idx.begin(), v_idx.end(), std::size_t(0));
		std::for_each(std::execution::par, v_idx.begin(), v_idx.end(),
			[&](const std::size_t i) { v_optimized[i] = TextureOptimizer::Load(v_paths[i]); });
	}

	// ---- Images / textures: identical pixel content is stored once
	json v_images = json::array(), v_textures = json::array();
	std::unordered_map<std::wstring, std::size_t> v_pathSlot;
	std::unordered_map<std::uint64_t, int> v_textureByHash;
	std::map<std::string, int> v_kindCount;
	std::size_t v_textureBytes = 0;
	for (std::size_t i = 0; i < v_paths.size(); i++)
	{
		v_pathSlot.emplace(v_paths[i], i);
		const OptimizedTexture& t = v_optimized[i];
		if (!t.ok || v_textureByHash.contains(t.hash)) continue;

		const int v_view = v_glb.AddBufferView(t.png.data(), t.png.size(), 0);
		v_images.push_back({ { "bufferView", v_view }, { "mimeType", "image/png" }, { "name", String::ToUtf8(FileStem(v_paths[i])) },
			{ "extras", { { "sm_source_size", { t.width, t.height } }, { "sm_encoding", t.kind } } } });
		v_textures.push_back({ { "source", static_cast<int>(v_images.size() - 1) }, { "sampler", 0 } });
		v_textureByHash.emplace(t.hash, static_cast<int>(v_textures.size() - 1));
		v_kindCount[t.kind]++;
		v_textureBytes += t.png.size();
	}

	const auto v_texIndex = [&](const std::wstring& path) -> int {
		if (path.empty()) return -1;
		const auto it = v_pathSlot.find(path);
		if (it == v_pathSlot.end() || !v_optimized[it->second].ok) return -1;
		return v_textureByHash.at(v_optimized[it->second].hash);
	};
	const auto v_texHash = [&](const std::wstring& path) -> std::string {
		const int t = v_texIndex(path);
		return (t < 0) ? (path.empty() ? std::string("-") : "missing:" + String::ToUtf8(path)) : std::to_string(t);
	};

	// ---- Materials: unused ones are dropped, identical ones (same texture content + shader + surface) merged
	json v_materials = json::array();
	std::vector<int> v_matRemap(v_matCount, -1);
	std::unordered_map<std::string, int> v_matByKey;
	std::size_t v_merged = 0;
	for (std::size_t mIdx = 0; mIdx < v_matCount; mIdx++)
	{
		if (!v_used[mIdx]) continue;
		const CapturedMaterial& m = GeometryCapture::Materials[mIdx];

		const std::string v_key = v_texHash(m.dif) + "|" + v_texHash(m.asg) + "|" + v_texHash(m.nor)
			+ "|" + std::to_string(m.shader) + "|" + m.surface;
		const auto v_existing = v_matByKey.find(v_key);
		if (v_existing != v_matByKey.end())
		{
			v_matRemap[mIdx] = v_existing->second;
			v_merged++;
			continue;
		}

		const int v_dif = v_texIndex(m.dif);
		const int v_asg = v_texIndex(m.asg);
		const int v_nor = v_texIndex(m.nor);

		std::string v_name = std::string("SM_") + ShaderName(m.shader);
		if (!m.dif.empty()) v_name += "_" + String::ToUtf8(FileStem(m.dif));
		if (!m.surface.empty()) v_name += "_" + m.surface;

		json v_mat;
		v_mat["name"] = v_name;
		v_mat["doubleSided"] = true;

		json v_pbr = { { "metallicFactor", 0.0 }, { "roughnessFactor", 0.6 } };
		if (v_dif >= 0) v_pbr["baseColorTexture"] = { { "index", v_dif } };
		v_mat["pbrMetallicRoughness"] = v_pbr;
		if (v_nor >= 0) v_mat["normalTexture"] = { { "index", v_nor } };

		switch (m.shader)
		{
		case 3: case 4: case 5: case 11:
			v_mat["alphaMode"] = "MASK";
			v_mat["alphaCutoff"] = 0.5;
			break;
		case 2: case 6: case 7: case 8: case 9: case 10:
			v_mat["alphaMode"] = "BLEND";
			break;
		default:
			// The diffuse alpha of DifAsgNor materials is the paint mask, not transparency
			v_mat["alphaMode"] = "OPAQUE";
			break;
		}

		json v_extras = {
			{ "sm_shader", m.shader },
			{ "sm_shader_name", ShaderName(m.shader) },
			{ "sm_dif", String::ToUtf8(m.dif) },
			{ "sm_asg", String::ToUtf8(m.asg) },
			{ "sm_nor", String::ToUtf8(m.nor) },
			{ "sm_surface", m.surface }
		};
		if (v_dif >= 0) v_extras["sm_dif_texture"] = v_dif;
		if (v_asg >= 0) v_extras["sm_asg_texture"] = v_asg;
		if (v_nor >= 0) v_extras["sm_nor_texture"] = v_nor;
		v_mat["extras"] = v_extras;

		v_matByKey.emplace(v_key, static_cast<int>(v_materials.size()));
		v_matRemap[mIdx] = static_cast<int>(v_materials.size());
		v_materials.push_back(std::move(v_mat));
	}

	std::size_t v_unusedMaterials = 0;
	for (std::size_t m = 0; m < v_matCount; m++) if (!v_used[m]) v_unusedMaterials++;
	const json v_optStats = {
		{ "materials_captured", v_matCount },
		{ "materials_unused_dropped", v_unusedMaterials },
		{ "materials_merged_identical", v_merged },
		{ "materials_written", v_materials.size() },
		{ "texture_files", v_paths.size() },
		{ "textures_written", v_textures.size() },
		{ "texture_encodings", v_kindCount },
		{ "texture_bytes", v_textureBytes }
	};

	// ---- Meshes / nodes
	json v_meshes = json::array(), v_nodes = json::array();
	json v_childNodes = json::array();

	std::vector<int> v_meshIndexOf(meshes.size(), -1);
	for (std::size_t v_meshNo = 0; v_meshNo < meshes.size(); v_meshNo++)
	{
		const CapturedMesh& v_mesh = meshes[v_meshNo];
		if (v_mesh.faces.empty()) continue;

		// Geometry is captured in output space; express it in the node frame
		const glm::mat4 v_toLocal = glm::inverse(v_mesh.world);
		const glm::mat3 v_toLocalRot = glm::mat3(v_toLocal);

		// Faces grouped by (merged) output material, in a stable order
		std::map<std::uint32_t, std::vector<std::uint32_t>> v_byMaterial;
		for (std::uint32_t a = 0; a < v_mesh.faces.size(); a++)
		{
			const int v_out = v_matRemap[v_mesh.faces[a].material];
			if (v_out >= 0) v_byMaterial[static_cast<std::uint32_t>(v_out)].push_back(a);
		}

		json v_primitives = json::array();
		for (auto& [v_matIdx, v_faceList] : v_byMaterial)
		{
			std::vector<glm::vec3> v_pos, v_nor;
			std::vector<glm::vec2> v_uv;
			std::vector<glm::vec4> v_col;
			std::vector<float> v_shape;
			std::vector<std::uint32_t> v_indices;
			std::unordered_map<VertexKey, std::uint32_t, VertexKeyHasher> v_vertMap;

			for (const std::uint32_t fIdx : v_faceList)
			{
				const CapturedFace& f = v_mesh.faces[fIdx];

				// Face normal fallback for corners without normals
				glm::vec3 v_faceNormal(0.0f);
				for (std::uint32_t a = 0; a < f.cornerCount; a++)
				{
					const glm::vec3& p = v_mesh.corners[f.firstCorner + a].position;
					const glm::vec3& q = v_mesh.corners[f.firstCorner + (a + 1) % f.cornerCount].position;
					v_faceNormal += glm::vec3((p.y - q.y) * (p.z + q.z), (p.z - q.z) * (p.x + q.x), (p.x - q.x) * (p.y + q.y));
				}
				const float v_fnLen = glm::length(v_faceNormal);
				v_faceNormal = (v_fnLen > 1e-12f) ? v_faceNormal / v_fnLen : glm::vec3(0.0f, 0.0f, 1.0f);

				const glm::vec4 v_color(
					SrgbToLinear(f.color.r / 255.0f),
					SrgbToLinear(f.color.g / 255.0f),
					SrgbToLinear(f.color.b / 255.0f),
					1.0f);

				std::uint32_t v_cornerIdx[64];
				const std::uint32_t v_count = std::min<std::uint32_t>(f.cornerCount, 64);
				for (std::uint32_t a = 0; a < v_count; a++)
				{
					const CapturedCorner& c = v_mesh.corners[f.firstCorner + a];
					const glm::vec3 v_n = (glm::dot(c.normal, c.normal) > 0.5f) ? c.normal : v_faceNormal;

					const glm::vec3 v_localPos = glm::vec3(v_toLocal * glm::vec4(c.position, 1.0f));
					const glm::vec3 v_localNor = glm::normalize(v_toLocalRot * v_n);
					VertexKey v_key{ ToGltf(v_localPos), ToGltf(v_localNor), glm::vec2(c.uv.x, 1.0f - c.uv.y), f.color.m_color, f.shape };
					auto [v_iter, v_inserted] = v_vertMap.try_emplace(v_key, static_cast<std::uint32_t>(v_pos.size()));
					if (v_inserted)
					{
						v_pos.push_back(v_key.pos);
						v_nor.push_back(v_key.nor);
						v_uv.push_back(v_key.uv);
						v_col.push_back(v_color);
						v_shape.push_back(static_cast<float>(f.shape));
					}
					v_cornerIdx[a] = v_iter->second;
				}

				for (std::uint32_t a = 1; a + 1 < v_count; a++)
				{
					v_indices.push_back(v_cornerIdx[0]);
					v_indices.push_back(v_cornerIdx[a]);
					v_indices.push_back(v_cornerIdx[a + 1]);
				}
			}

			json v_attribs = {
				{ "POSITION", v_glb.AddAccessor(v_pos, GL_FLOAT, "VEC3", GL_ARRAY_BUFFER, true) },
				{ "NORMAL", v_glb.AddAccessor(v_nor, GL_FLOAT, "VEC3", GL_ARRAY_BUFFER) },
				{ "TEXCOORD_0", v_glb.AddAccessor(v_uv, GL_FLOAT, "VEC2", GL_ARRAY_BUFFER) },
				{ "COLOR_0", v_glb.AddAccessor(v_col, GL_FLOAT, "VEC4", GL_ARRAY_BUFFER) },
				{ "_SM_SHAPE", v_glb.AddAccessor(v_shape, GL_FLOAT, "SCALAR", GL_ARRAY_BUFFER) }
			};

			v_primitives.push_back({
				{ "attributes", v_attribs },
				{ "indices", v_glb.AddAccessor(v_indices, GL_UNSIGNED_INT, "SCALAR", GL_ELEMENT_ARRAY_BUFFER) },
				{ "material", v_matIdx },
				{ "mode", 4 }
			});
		}

		v_meshes.push_back({ { "name", v_mesh.name }, { "primitives", v_primitives } });
		v_meshIndexOf[v_meshNo] = static_cast<int>(v_meshes.size() - 1);
	}

	// One node per captured mesh (also empty ones, so the hierarchy stays intact)
	// glTF frame = C * sm frame * C^-1, C: (x, y, z) -> (x, z, -y)
	const glm::mat4 v_c(
		glm::vec4(1, 0, 0, 0),
		glm::vec4(0, 0, -1, 0),
		glm::vec4(0, 1, 0, 0),
		glm::vec4(0, 0, 0, 1));
	const glm::mat4 v_cInv = glm::inverse(v_c);

	for (std::size_t v_meshNo = 0; v_meshNo < meshes.size(); v_meshNo++)
	{
		const CapturedMesh& v_mesh = meshes[v_meshNo];
		const glm::mat4 v_parentWorld = (v_mesh.parent >= 0) ? meshes[v_mesh.parent].world : glm::mat4(1.0f);
		const glm::mat4 v_local = v_c * (glm::inverse(v_parentWorld) * v_mesh.world) * v_cInv;

		json v_node = { { "name", v_mesh.name } };
		if (v_meshIndexOf[v_meshNo] >= 0)
			v_node["mesh"] = v_meshIndexOf[v_meshNo];

		const glm::vec3 v_t(v_local[3]);
		const glm::quat v_q = glm::normalize(glm::quat_cast(glm::mat3(v_local)));
		if (glm::length(v_t) > 0.0f)
			v_node["translation"] = json::array({ v_t.x, v_t.y, v_t.z });
		if (std::abs(v_q.w) < 0.999999f)
			v_node["rotation"] = json::array({ v_q.x, v_q.y, v_q.z, v_q.w });

		json v_extras = json::object();
		if (!v_mesh.extrasJson.empty())
			v_extras = json::parse(v_mesh.extrasJson);
		v_extras["sm_object"] = v_mesh.name;
		v_node["extras"] = v_extras;

		v_nodes.push_back(std::move(v_node));
	}

	for (std::size_t v_meshNo = 0; v_meshNo < meshes.size(); v_meshNo++)
	{
		const int v_parent = meshes[v_meshNo].parent;
		if (v_parent >= 0)
		{
			if (!v_nodes[v_parent].contains("children")) v_nodes[v_parent]["children"] = json::array();
			v_nodes[v_parent]["children"].push_back(static_cast<int>(v_meshNo));
		}
		else
		{
			v_childNodes.push_back(static_cast<int>(v_meshNo));
		}
	}

	// Root node
	json v_shapes = json::array();
	for (const CapturedShape& s : GeometryCapture::Shapes)
	{
		v_shapes.push_back({
			{ "uuid", s.uuid.toString() },
			{ "color", s.color.StringHex() },
			{ "type", static_cast<int>(s.type) },
			{ "child", (s.childIndex == std::size_t(-1)) ? -1 : static_cast<long long>(s.childIndex) }
		});
	}

	v_nodes.push_back({
		{ "name", sceneName },
		{ "children", v_childNodes },
		{ "extras", { { "sm_blueprint", sceneName }, { "sm_shapes", v_shapes }, { "sm_optimization", v_optStats } } }
	});
	if (!rootExtrasJson.empty())
		v_nodes.back()["extras"].update(json::parse(rootExtrasJson));

	// Surface presets travel with the file, so Blender / Unity know what each sm_surface means
	{
		std::ifstream v_presetFile("./Resources/SurfacePresets.json", std::ios::binary);
		if (v_presetFile.is_open())
		{
			const json v_presets = json::parse(v_presetFile, nullptr, false, true);
			if (!v_presets.is_discarded() && v_presets.contains("surfaces"))
				v_nodes.back()["extras"]["sm_surface_presets"] = v_presets["surfaces"];
		}
	}
	const int v_rootNode = static_cast<int>(v_nodes.size() - 1);

	v_root["scene"] = 0;
	v_root["scenes"] = json::array({ { { "name", sceneName }, { "nodes", json::array({ v_rootNode }) } } });
	v_root["nodes"] = v_nodes;
	v_root["meshes"] = v_meshes;
	v_root["materials"] = v_materials;
	if (!v_images.empty())
	{
		v_root["images"] = v_images;
		v_root["textures"] = v_textures;
		v_root["samplers"] = json::array({ { { "wrapS", 10497 }, { "wrapT", 10497 } } });
	}
	v_root["accessors"] = v_glb.accessors;
	v_root["bufferViews"] = v_glb.bufferViews;

	v_glb.Align4();
	v_root["buffers"] = json::array({ { { "byteLength", v_glb.bin.size() } } });

	// ---- GLB container
	std::string v_jsonStr = v_root.dump();
	while (v_jsonStr.size() % 4 != 0) v_jsonStr.push_back(' ');

	std::ofstream v_file(path, std::ios::binary);
	if (!v_file.is_open())
	{
		outError = "Couldn't create the glb file";
		return false;
	}

	const auto v_writeU32 = [&v_file](const std::uint32_t v) { v_file.write(reinterpret_cast<const char*>(&v), 4); };

	const std::uint32_t v_totalSize = 12 + 8 + static_cast<std::uint32_t>(v_jsonStr.size()) + 8 + static_cast<std::uint32_t>(v_glb.bin.size());
	v_writeU32(0x46546C67); // "glTF"
	v_writeU32(2);
	v_writeU32(v_totalSize);

	v_writeU32(static_cast<std::uint32_t>(v_jsonStr.size()));
	v_writeU32(0x4E4F534A); // "JSON"
	v_file.write(v_jsonStr.data(), v_jsonStr.size());

	v_writeU32(static_cast<std::uint32_t>(v_glb.bin.size()));
	v_writeU32(0x004E4942); // "BIN\0"
	v_file.write(reinterpret_cast<const char*>(v_glb.bin.data()), v_glb.bin.size());

	return v_file.good();
}

SM_MANAGED_CODE
