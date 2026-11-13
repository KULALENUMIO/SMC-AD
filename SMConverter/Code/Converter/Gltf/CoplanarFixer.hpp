#pragma once

#include "Converter/Gltf/GeometryCapture.hpp"

#include "Utils/clr_include.hpp"

SM_UNMANAGED_CODE

/*
	Resolves z-fighting between parts whose faces lie on the same plane, face the same way and overlap.

	For every group of overlapping coplanar faces (only faces of different shapes count as overlapping):
	  - each shape's area in the group is summed (rough "how much of this surface belongs to it")
	  - the shape with the largest area stays where it is
	  - the 2nd one is pushed back (against its normal) by `offset`, the 3rd by 2 * offset, ...
	Only the overlapping faces move; the rest of the part stays in place.
	Run it after HiddenFaceCuller: moving faces opens hairline slits that would make the culler keep interior faces.
*/
class CoplanarFixer
{
public:
	struct Stats
	{
		std::size_t groups = 0;      // overlapping coplanar groups that were resolved
		std::size_t facesMoved = 0;
		std::size_t maxDepth = 0;    // deepest rank used (1 = one face pushed once)
	};

	// offset in output units (metres)
	static Stats Fix(CapturedMesh& mesh, const float offset);

private:
	CoplanarFixer() = delete;
};

SM_MANAGED_CODE
