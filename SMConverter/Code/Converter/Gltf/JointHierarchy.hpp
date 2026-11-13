#pragma once

#include "Converter/Gltf/GeometryCapture.hpp"

#include "Utils/clr_include.hpp"

#include <string>
#include <vector>

SM_UNMANAGED_CODE

class SMBlueprint;
class SMBody;

/*
	Turns the flat list of joint-separated bodies into a hierarchy:
	- the body with the most shapes is the root
	- every other body connected through a joint becomes a child of the body on the other side
	- the child's node frame sits on the joint pivot (centre of the joint cell) with its local +Z
	  pointing along the joint axis (bearing rotation axis / piston direction), so in Blender
	  rotating the object around its local Z behaves like the bearing
	Joint metadata is written into the node extras; every joint (including ones that close loops)
	is listed in the returned root extras.
*/
class JointHierarchy
{
public:
	// meshBodies[i] = body the mesh was captured from (nullptr if none). Returns root extras (JSON object text).
	static std::string Build(
		SMBlueprint* pBlueprint,
		std::vector<CapturedMesh>& meshes,
		const std::vector<const SMBody*>& meshBodies,
		const glm::mat4& blueprintTransform);

private:
	JointHierarchy() = delete;
};

SM_MANAGED_CODE
