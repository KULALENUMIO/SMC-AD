#include "JointHierarchy.hpp"

#include "Converter/Entity/Blueprint.hpp"
#include "Converter/Entity/Joint.hpp"
#include "Converter/Entity/Body.hpp"

#include "ObjectDatabase/ObjectRotations.hpp"
#include "ObjectDatabase/ObjectData.hpp"

#include <nlohmann/json.hpp>

#include <unordered_map>
#include <map>

SM_UNMANAGED_CODE

namespace
{
	using json = nlohmann::json;

	struct JointInfo
	{
		const SMJoint* joint;
		std::size_t bodyA;
		std::size_t bodyB;
		glm::vec3 pivot;  // output space
		glm::mat3 frame;  // output space, orthonormal; column 2 = joint axis
	};

	const char* JointTypeName(const std::uint8_t type)
	{
		switch (type)
		{
		case PartData::Joint_Bearing: return "bearing";
		case PartData::Joint_Piston:  return "piston";
		case PartData::Joint_Spring:  return "suspension";
		default:                      return "joint";
		}
	}

	json Vec3Json(const glm::vec3& v)
	{
		return json::array({ v.x, v.y, v.z });
	}

	void CollectJoints(const std::vector<SMEntity*>& objects, std::vector<const SMJoint*>& out)
	{
		for (const SMEntity* v_pEntity : objects)
		{
			if (v_pEntity->Type() == EntityType::Joint)
				out.push_back(static_cast<const SMJoint*>(v_pEntity));
			else if (v_pEntity->Type() == EntityType::Body)
				CollectJoints(static_cast<const SMBody*>(v_pEntity)->GetObjects(), out);
		}
	}
}

std::string JointHierarchy::Build(
	SMBlueprint* pBlueprint,
	std::vector<CapturedMesh>& meshes,
	const std::vector<const SMBody*>& meshBodies,
	const glm::mat4& blueprintTransform)
{
	const auto& v_childToBody = pBlueprint->m_childToBodyIndex;

	// 1. Physical body of every mesh (majority of its non-joint shapes)
	std::unordered_map<std::size_t, int> v_meshOfBody;
	std::unordered_map<std::size_t, std::size_t> v_shapeCount;
	for (const auto& [v_child, v_body] : v_childToBody)
		v_shapeCount[v_body]++;

	for (std::size_t a = 0; a < meshes.size() && a < meshBodies.size(); a++)
	{
		if (!meshBodies[a]) continue;

		std::map<std::size_t, std::size_t> v_votes;
		for (const SMEntity* v_pEntity : meshBodies[a]->GetObjects())
		{
			if (v_pEntity->Type() == EntityType::Joint) continue;

			const auto v_iter = v_childToBody.find(v_pEntity->GetIndex());
			if (v_iter != v_childToBody.end())
				v_votes[v_iter->second]++;
		}

		if (v_votes.empty()) continue;

		const auto v_best = std::max_element(v_votes.begin(), v_votes.end(),
			[](const auto& l, const auto& r) { return l.second < r.second; });

		meshes[a].bodyIndex = static_cast<long long>(v_best->first);
		v_meshOfBody.emplace(v_best->first, static_cast<int>(a));
	}

	// 2. Joints in output space
	std::vector<const SMJoint*> v_joints;
	CollectJoints(pBlueprint->m_objects, v_joints);

	std::vector<JointInfo> v_infos;
	json v_allJoints = json::array();

	for (const SMJoint* v_pJoint : v_joints)
	{
		const auto v_itA = v_childToBody.find(v_pJoint->GetIndex());
		const auto v_itB = v_childToBody.find(v_pJoint->m_childB);
		if (v_itA == v_childToBody.end() || v_itB == v_childToBody.end()) continue;

		const glm::mat4 v_toOutput = blueprintTransform * v_pJoint->GetPreTransform();
		const glm::mat4 v_rotation = Rotations::GetRotationMatrix(v_pJoint->m_jointRotation);

		JointInfo v_info;
		v_info.joint = v_pJoint;
		v_info.bodyA = v_itA->second;
		v_info.bodyB = v_itB->second;
		v_info.pivot = glm::vec3(v_toOutput * glm::vec4(v_pJoint->m_jointPos + glm::vec3(0.5f), 1.0f));

		const glm::mat3 v_axes = glm::mat3(v_toOutput) * glm::mat3(v_rotation);
		v_info.frame = glm::mat3(
			glm::normalize(v_axes[0]),
			glm::normalize(v_axes[1]),
			glm::normalize(v_axes[2]));

		const std::uint8_t v_type = v_pJoint->GetPartData()->m_jointType;
		v_allJoints.push_back({
			{ "id", (v_pJoint->m_jointId == std::size_t(-1)) ? -1 : static_cast<long long>(v_pJoint->m_jointId) },
			{ "type", JointTypeName(v_type) },
			{ "uuid", v_pJoint->GetUuid().toString() },
			{ "body_a", v_info.bodyA },
			{ "body_b", v_info.bodyB },
			{ "pivot", Vec3Json(v_info.pivot) },
			{ "axis", Vec3Json(v_info.frame[2]) }
		});

		if (v_info.bodyA != v_info.bodyB)
			v_infos.push_back(v_info);
	}

	// 3. Root = mesh body with the most shapes
	std::size_t v_root = std::size_t(-1);
	std::size_t v_rootCount = 0;
	for (const auto& [v_body, v_meshIdx] : v_meshOfBody)
	{
		const std::size_t v_count = v_shapeCount[v_body];
		if (v_root == std::size_t(-1) || v_count > v_rootCount || (v_count == v_rootCount && v_body < v_root))
		{
			v_root = v_body;
			v_rootCount = v_count;
		}
	}

	// 4. BFS spanning tree over the joint graph
	std::unordered_map<std::size_t, std::vector<std::size_t>> v_adjacency; // body -> joint info indices
	for (std::size_t a = 0; a < v_infos.size(); a++)
	{
		v_adjacency[v_infos[a].bodyA].push_back(a);
		v_adjacency[v_infos[a].bodyB].push_back(a);
	}

	std::unordered_map<std::size_t, std::pair<std::size_t, std::size_t>> v_parentOf; // body -> (parent body, joint info)
	std::vector<std::size_t> v_queue;
	if (v_root != std::size_t(-1))
	{
		v_parentOf[v_root] = { v_root, std::size_t(-1) };
		v_queue.push_back(v_root);
	}

	for (std::size_t q = 0; q < v_queue.size(); q++)
	{
		const std::size_t v_cur = v_queue[q];
		for (const std::size_t v_jIdx : v_adjacency[v_cur])
		{
			const JointInfo& j = v_infos[v_jIdx];
			const std::size_t v_other = (j.bodyA == v_cur) ? j.bodyB : j.bodyA;
			if (v_parentOf.contains(v_other)) continue;

			v_parentOf[v_other] = { v_cur, v_jIdx };
			v_queue.push_back(v_other);
		}
	}

	// 5. Node frames, parents and extras
	for (std::size_t a = 0; a < meshes.size(); a++)
	{
		CapturedMesh& v_mesh = meshes[a];
		json v_extras = json::object();
		if (v_mesh.bodyIndex >= 0)
			v_extras["sm_body"] = v_mesh.bodyIndex;

		const auto v_parentIt = (v_mesh.bodyIndex >= 0) ? v_parentOf.find(static_cast<std::size_t>(v_mesh.bodyIndex)) : v_parentOf.end();
		if (v_parentIt == v_parentOf.end())
		{
			if (v_mesh.bodyIndex >= 0) v_extras["sm_connected"] = false;
		}
		else if (v_parentIt->second.second == std::size_t(-1))
		{
			v_extras["sm_root"] = true;
		}
		else
		{
			const JointInfo& j = v_infos[v_parentIt->second.second];

			glm::mat4 v_world(j.frame);
			v_world[3] = glm::vec4(j.pivot, 1.0f);
			v_mesh.world = v_world;

			const auto v_parentMesh = v_meshOfBody.find(v_parentIt->second.first);
			v_mesh.parent = (v_parentMesh != v_meshOfBody.end()) ? v_parentMesh->second : -1;

			const std::uint8_t v_type = j.joint->GetPartData()->m_jointType;
			v_extras["sm_joint"] = {
				{ "id", (j.joint->m_jointId == std::size_t(-1)) ? -1 : static_cast<long long>(j.joint->m_jointId) },
				{ "type", JointTypeName(v_type) },
				{ "uuid", j.joint->GetUuid().toString() },
				{ "parent_body", v_parentIt->second.first },
				{ "axis", "Z" },
				{ "pivot", Vec3Json(j.pivot) },
				{ "axis_world", Vec3Json(j.frame[2]) }
			};
		}

		v_mesh.extrasJson = v_extras.dump();
	}

	return json{
		{ "sm_root_body", (v_root == std::size_t(-1)) ? -1 : static_cast<long long>(v_root) },
		{ "sm_joints", v_allJoints }
	}.dump();
}

SM_MANAGED_CODE
