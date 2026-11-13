#pragma once

#include "Utils/clr_include.hpp"

#include <cstdint>
#include <string>
#include <vector>

SM_UNMANAGED_CODE

/*
	Lossless texture optimisation for the glTF export. Every step keeps the decoded pixel values identical:
	  - constant images (every pixel the same) become 1x1 (sampling gives the same value everywhere)
	  - an alpha channel that is 255 everywhere is dropped (RGB)
	  - images with <= 256 distinct RGBA values are stored as indexed PNG (+ tRNS for alpha)
	  - PNG is written with zlib compression (default level, or maximum with -maxtexcompression)
	The content hash (size + RGBA pixels) lets the writer merge identical textures and materials.
*/
struct OptimizedTexture
{
	bool ok = false;
	std::uint64_t hash = 0;     // content hash of the decoded RGBA pixels
	std::vector<std::uint8_t> png;
	std::uint32_t width = 0;     // source size
	std::uint32_t height = 0;
	const char* kind = "";       // "constant", "palette", "rgb", "rgba"
};

class TextureOptimizer
{
public:
	static OptimizedTexture Load(const std::wstring& path);

private:
	TextureOptimizer() = delete;
};

SM_MANAGED_CODE
