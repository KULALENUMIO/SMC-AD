#pragma once

#include "Utils/clr_include.hpp"

SM_UNMANAGED_CODE

struct TileConverterSettings
{
	inline static bool Export8kGroundTextures = false;
	inline static bool ExportGroundTextures = true;
	inline static bool ExportHarvestables = true;
	inline static bool ExportBlueprints = true;
	inline static bool ExportKinematics = true;
	inline static bool ExportClutter = true;
	inline static bool ExportPrefabs = true;
	inline static bool ExportAssets = true;
	inline static bool ExportDecals = false;

private:
	TileConverterSettings() = default;
	TileConverterSettings(const TileConverterSettings&) = delete;
	TileConverterSettings(TileConverterSettings&&) = delete;
	~TileConverterSettings() = default;
};

struct BlueprintConverterSettings
{
	inline static int SeparationType = 0;
	inline static bool ApplyControllerPresets = false;
	// See BlockOpt_* in Converter/BlockMesher/BlockMesher.hpp
	inline static int BlockOptimization = 0;
	// Write a self contained .glb instead of .obj/.mtl
	inline static bool ExportGltf = false;
	// Remove faces that can't be seen from outside of each output object (glTF export only)
	inline static bool CullHiddenFaces = false;
	// Push back overlapping coplanar faces of different parts to stop z-fighting (glTF export only)
	inline static bool FixCoplanar = false;
	// Distance per rank in metres (0.3 mm)
	inline static float CoplanarOffset = 0.0003f;
	// Texture PNG compression: false = zlib default (fast), true = maximum (~6% smaller, ~5x slower)
	inline static bool MaxTextureCompression = false;

private:
	BlueprintConverterSettings() = default;
	BlueprintConverterSettings(const BlueprintConverterSettings&) = delete;
	BlueprintConverterSettings(BlueprintConverterSettings&&) = delete;
	~BlueprintConverterSettings() = default;
};

struct SharedConverterSettings
{
	inline static bool ExportMaterials     = true;
	inline static bool ExportNormals       = true;
	inline static bool ExportUvs           = true;
	inline static bool DeduplicateVertices = false;

private:
	SharedConverterSettings() = default;
	SharedConverterSettings(const SharedConverterSettings&) = delete;
	SharedConverterSettings(SharedConverterSettings&&) = delete;
	~SharedConverterSettings() = default;
};

SM_MANAGED_CODE