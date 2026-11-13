#pragma once

#include "Converter/Entity/Entity.hpp"

#include "Utils/clr_include.hpp"

#include <unordered_map>
#include <string>
#include <vector>

SM_UNMANAGED_CODE

struct Model;

// One face corner in blueprint (output) space
struct CapturedCorner
{
	glm::vec3 position;
	glm::vec3 normal;
	glm::vec2 uv;
};

struct CapturedFace
{
	std::uint32_t firstCorner;
	std::uint32_t cornerCount;
	std::uint32_t material; // index into GeometryCapture::Materials
	std::uint32_t shape;    // index into GeometryCapture::Shapes
	SMColor color;          // paint color
};

// Geometry of one output object (a body / separation group)
struct CapturedMesh
{
	std::string name;
	std::vector<CapturedCorner> corners;
	std::vector<CapturedFace> faces;

	// Node placement (blueprint/output space). The geometry above is always in output space;
	// the writer expresses it relative to this frame.
	glm::mat4 world{ 1.0f };
	int parent = -1;          // index of the parent mesh (-1 = child of the scene root)
	long long bodyIndex = -1; // physical body index in the blueprint (-1 = unknown / mixed)
	std::string extrasJson;   // extra node metadata (JSON object text), may be empty
};

// A material is a texture set + SM shader type. Paint color is NOT part of it (it is stored per face).
struct CapturedMaterial
{
	std::wstring dif;
	std::wstring asg;
	std::wstring nor;
	std::size_t shader; // Resources/MaterialIds.json index
	// Surface type (metal_painted, metal_bare, rubber, plastic, wood, stone, glass, standard),
	// classified from the render material, its metalProfile and the shape's physicsMaterial
	std::string surface;

	// Glass, alpha, grass, leaves, water, light cone, liquids: these can be seen through
	bool IsTransparent() const noexcept;
};

struct CapturedShape
{
	SMUuid uuid;
	SMColor color;
	EntityType type;
	std::size_t childIndex; // index of the child / joint inside the blueprint json (-1 if not applicable)
};

class GeometryCapture
{
public:
	// Starts capturing into pTarget. textureMap must contain every material name the entities can produce.
	static void Begin(CapturedMesh* pTarget, const SMEntity::EntityTextureMap* pTextureMap);
	static void End();
	static bool IsActive() noexcept;

	// Called from Model::WriteToFile while capturing
	static void AddModel(const Model& model, const glm::mat4& modelMatrix, const SMEntity* pEntity);

	static void Reset();

	inline static std::vector<CapturedMaterial> Materials = {};
	inline static std::vector<CapturedShape> Shapes = {};

private:
	static std::uint32_t GetMaterialIndex(const SMTextureList& textures, const std::size_t shader, const std::string& surface);

public:
	static std::string ClassifySurface(const SMTextureList& textures, const std::size_t shader, const std::string_view& physicsMaterial);

private:
	static std::uint32_t GetShapeIndex(const SMEntity* pEntity);

	inline static CapturedMesh* s_target = nullptr;
	inline static const SMEntity::EntityTextureMap* s_textureMap = nullptr;
	inline static std::unordered_map<std::wstring, std::uint32_t> s_materialMap = {};
	inline static std::unordered_map<const SMEntity*, std::uint32_t> s_shapeMap = {};

	GeometryCapture() = delete;
};

SM_MANAGED_CODE
