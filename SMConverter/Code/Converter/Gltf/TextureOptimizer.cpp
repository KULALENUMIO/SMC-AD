#include "TextureOptimizer.hpp"

#include "Converter/ConvertSettings.hpp"

#include <FreeImage.h>

#include <unordered_map>
#include <cstring>

SM_UNMANAGED_CODE

namespace
{
	struct Bitmap
	{
		FIBITMAP* dib = nullptr;
		explicit Bitmap(FIBITMAP* d = nullptr) : dib(d) {}
		~Bitmap() { if (dib) FreeImage_Unload(dib); }
		Bitmap(const Bitmap&) = delete;
		Bitmap& operator=(const Bitmap&) = delete;
	};

	bool SavePng(FIBITMAP* dib, std::vector<std::uint8_t>& out)
	{
		FIMEMORY* v_mem = FreeImage_OpenMemory();
		const bool v_ok = FreeImage_SaveToMemory(FIF_PNG, dib, v_mem,
			BlueprintConverterSettings::MaxTextureCompression ? PNG_Z_BEST_COMPRESSION : PNG_Z_DEFAULT_COMPRESSION) != 0;
		if (v_ok)
		{
			BYTE* v_data = nullptr;
			DWORD v_size = 0;
			FreeImage_AcquireMemory(v_mem, &v_data, &v_size);
			out.assign(v_data, v_data + v_size);
		}
		FreeImage_CloseMemory(v_mem);
		return v_ok;
	}

	inline std::uint32_t PackRGBA(const BYTE* px)
	{
		return static_cast<std::uint32_t>(px[FI_RGBA_RED])
			| (static_cast<std::uint32_t>(px[FI_RGBA_GREEN]) << 8)
			| (static_cast<std::uint32_t>(px[FI_RGBA_BLUE]) << 16)
			| (static_cast<std::uint32_t>(px[FI_RGBA_ALPHA]) << 24);
	}

	inline void WriteRGBA(BYTE* px, const std::uint32_t c)
	{
		px[FI_RGBA_RED] = static_cast<BYTE>(c & 0xFF);
		px[FI_RGBA_GREEN] = static_cast<BYTE>((c >> 8) & 0xFF);
		px[FI_RGBA_BLUE] = static_cast<BYTE>((c >> 16) & 0xFF);
		px[FI_RGBA_ALPHA] = static_cast<BYTE>((c >> 24) & 0xFF);
	}
}

OptimizedTexture TextureOptimizer::Load(const std::wstring& path)
{
	OptimizedTexture v_result;
	if (path.empty()) return v_result;

	FREE_IMAGE_FORMAT v_fmt = FreeImage_GetFileTypeU(path.c_str(), 0);
	if (v_fmt == FIF_UNKNOWN) v_fmt = FreeImage_GetFIFFromFilenameU(path.c_str());
	if (v_fmt == FIF_UNKNOWN) return v_result;

	Bitmap v_source(FreeImage_LoadU(v_fmt, path.c_str(), 0));
	if (!v_source.dib) return v_result;

	Bitmap v_rgba(FreeImage_ConvertTo32Bits(v_source.dib));
	if (!v_rgba.dib) return v_result;

	const std::uint32_t w = FreeImage_GetWidth(v_rgba.dib);
	const std::uint32_t h = FreeImage_GetHeight(v_rgba.dib);
	if (w == 0 || h == 0) return v_result;
	v_result.width = w;
	v_result.height = h;

	// ---- scan: hash, constant, opaque, distinct colours
	std::uint64_t v_hash = 1469598103934665603ull;
	const auto v_mix = [&v_hash](std::uint32_t v) {
		for (int b = 0; b < 4; b++) { v_hash ^= (v >> (b * 8)) & 0xFF; v_hash *= 1099511628211ull; }
	};
	v_mix(w);
	v_mix(h);

	const std::uint32_t v_first = PackRGBA(FreeImage_GetScanLine(v_rgba.dib, 0));
	bool v_constant = true;
	bool v_opaque = true;
	std::unordered_map<std::uint32_t, std::uint8_t> v_palette;
	bool v_paletteOk = true;

	for (std::uint32_t y = 0; y < h; y++)
	{
		const BYTE* v_line = FreeImage_GetScanLine(v_rgba.dib, y);
		for (std::uint32_t x = 0; x < w; x++)
		{
			const std::uint32_t c = PackRGBA(v_line + x * 4);
			v_mix(c);
			if (c != v_first) v_constant = false;
			if ((c >> 24) != 0xFF) v_opaque = false;
			if (v_paletteOk && v_palette.find(c) == v_palette.end())
			{
				if (v_palette.size() >= 256) v_paletteOk = false;
				else v_palette.emplace(c, static_cast<std::uint8_t>(v_palette.size()));
			}
		}
	}
	v_result.hash = v_hash;

	// ---- encode
	if (v_constant)
	{
		Bitmap v_one(FreeImage_Allocate(1, 1, 32));
		WriteRGBA(FreeImage_GetScanLine(v_one.dib, 0), v_first);
		if (v_opaque)
		{
			Bitmap v_rgb(FreeImage_ConvertTo24Bits(v_one.dib));
			v_result.ok = SavePng(v_rgb.dib, v_result.png);
		}
		else
		{
			v_result.ok = SavePng(v_one.dib, v_result.png);
		}
		v_result.kind = "constant";
		return v_result;
	}

	if (v_paletteOk)
	{
		Bitmap v_indexed(FreeImage_Allocate(w, h, 8));
		RGBQUAD* v_pal = FreeImage_GetPalette(v_indexed.dib);
		std::vector<BYTE> v_alpha(256, 0xFF);
		for (const auto& [c, idx] : v_palette)
		{
			v_pal[idx].rgbRed = static_cast<BYTE>(c & 0xFF);
			v_pal[idx].rgbGreen = static_cast<BYTE>((c >> 8) & 0xFF);
			v_pal[idx].rgbBlue = static_cast<BYTE>((c >> 16) & 0xFF);
			v_pal[idx].rgbReserved = 0;
			v_alpha[idx] = static_cast<BYTE>((c >> 24) & 0xFF);
		}
		// Unused palette entries: opaque black (never referenced)
		for (std::size_t i = v_palette.size(); i < 256; i++)
		{
			v_pal[i].rgbRed = v_pal[i].rgbGreen = v_pal[i].rgbBlue = 0;
			v_pal[i].rgbReserved = 0;
		}
		if (!v_opaque)
			FreeImage_SetTransparencyTable(v_indexed.dib, v_alpha.data(), static_cast<int>(v_palette.size()));

		for (std::uint32_t y = 0; y < h; y++)
		{
			const BYTE* v_src = FreeImage_GetScanLine(v_rgba.dib, y);
			BYTE* v_dst = FreeImage_GetScanLine(v_indexed.dib, y);
			for (std::uint32_t x = 0; x < w; x++)
				v_dst[x] = v_palette[PackRGBA(v_src + x * 4)];
		}

		v_result.ok = SavePng(v_indexed.dib, v_result.png);
		v_result.kind = "palette";
		return v_result;
	}

	if (v_opaque)
	{
		Bitmap v_rgb(FreeImage_ConvertTo24Bits(v_rgba.dib));
		v_result.ok = v_rgb.dib && SavePng(v_rgb.dib, v_result.png);
		v_result.kind = "rgb";
		return v_result;
	}

	v_result.ok = SavePng(v_rgba.dib, v_result.png);
	v_result.kind = "rgba";
	return v_result;
}

SM_MANAGED_CODE
