#pragma once

#include "Converter/Entity/Entity.hpp"

#include "Utils/clr_include.hpp"

#include <memory>

SM_UNMANAGED_CODE

class SMBlueprint;
class BlockData;
struct Model;

// Values of BlueprintConverterSettings::BlockOptimization
enum : std::uint8_t
{
	BlockOpt_None  = 0, // Every block is written as an individual cube (original behaviour)
	BlockOpt_Cull  = 1, // Faces hidden between touching blocks are removed (watertight, 1 quad per visible cell face)
	BlockOpt_Merge = 2  // Cull + coplanar faces with the same material are merged into rectangles (greedy meshing)
};

/*
	Holds the optimized geometry of all blocks that share the same
	container (object group), physical body, block uuid and color.
	The vertices are stored in body space, so the transform is identity.
*/
class SMMergedBlocks final : public SMEntity
{
public:
	// Child index of one of the merged blocks (all of them are in the same body), so the merged mesh is
	// counted for its body like the blocks were (joint hierarchy, body lookup)
	std::size_t m_index = std::size_t(-1);
	std::size_t GetIndex() const override { return m_index; }

	SMMergedBlocks(const BlockData* pParent, const SMColor color, const glm::mat4& preTransform);
	~SMMergedBlocks();

	SMMergedBlocks(const SMMergedBlocks&) = delete;
	SMMergedBlocks(SMMergedBlocks&&) = delete;

	EntityType Type() const override;
	const SMUuid& GetUuid() const override;
	SMColor GetColor() const override;
	std::string_view GetPhysicsMaterial() const override;
	char* GetMtlNameCStr(const std::string_view& material, const std::size_t idx, char* pCString) const override;
	std::string GetMtlName(const std::size_t idx) const override;
	void FillTextureMap(EntityTextureMap& textureMap) const override;
	void WriteObjectToFile(std::ofstream& file, WriterOffsetData& offset, const glm::mat4& transform) const override;
	glm::mat4 GetTransformMatrix() const override;

	Model& GetModel() noexcept;

private:
	const BlockData* m_parent;
	SMColor m_color;
	std::unique_ptr<Model> m_model;
};

class BlockMesher
{
public:
	struct Stats
	{
		std::size_t blocksIn = 0;
		std::size_t cubeFacesIn = 0; // 6 per block cell (what the original exporter would write as 12 triangles)
		std::size_t quadsOut = 0;
		std::size_t entitiesOut = 0;
	};

	// Replaces every SMBlock inside the blueprint with optimized SMMergedBlocks entities.
	// Must be called after all the transforms (controller presets, center point) have been calculated.
	static Stats Optimize(SMBlueprint* pBlueprint, const int mode);

private:
	BlockMesher() = default;
	BlockMesher(const BlockMesher&) = delete;
	BlockMesher(BlockMesher&&) = delete;
	~BlockMesher() = default;
};

SM_MANAGED_CODE
