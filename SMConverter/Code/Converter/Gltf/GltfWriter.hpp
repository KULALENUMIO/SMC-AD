#pragma once

#include "Converter/Gltf/GeometryCapture.hpp"

#include "Utils/clr_include.hpp"

#include <string>
#include <vector>

SM_UNMANAGED_CODE

/*
	Writes a self contained binary glTF (.glb):
	- one node per CapturedMesh (body / separation group), one primitive per material
	- materials = texture set + SM shader; textures are converted to PNG and embedded
	- per vertex: POSITION, NORMAL, TEXCOORD_0, COLOR_0 (paint color, linear), _SM_SHAPE (shape index)
	- SM specific data is stored in "extras" (shader ids, ASG texture, shape table)
	Coordinates: SM is Z-up, glTF is Y-up -> (x, y, z)_sm = (x, -z, y)_gltf
*/
class GltfWriter
{
public:
	static bool Write(
		const std::wstring& path,
		const std::string& sceneName,
		const std::vector<CapturedMesh>& meshes,
		const std::string& rootExtrasJson,
		std::string& outError);

private:
	GltfWriter() = delete;
};

SM_MANAGED_CODE
