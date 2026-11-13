#pragma once

#include "Converter/Gltf/GeometryCapture.hpp"

#include "Utils/clr_include.hpp"

SM_UNMANAGED_CODE

/*
	Removes faces that cannot be seen from outside of the mesh.

	A face is kept when, from at least one of its sample points and from at least one of its two sides,
	a ray in some direction escapes the mesh without hitting an occluder.
	Faces with see-through materials (glass, alpha, foliage, liquids) never act as occluders.

	The test is done per CapturedMesh (one output object), so faces are never removed because of
	geometry that belongs to another (possibly moving) body.
*/
class HiddenFaceCuller
{
public:
	struct Stats
	{
		std::size_t facesIn = 0;
		std::size_t facesOut = 0;
		std::size_t keptByRaster = 0; // faces the inside-out test would have removed but are visible from outside
	};

	static Stats Cull(CapturedMesh& mesh);

private:
	HiddenFaceCuller() = delete;
};

SM_MANAGED_CODE
