#include "CoplanarFixer.hpp"

#include <unordered_map>
#include <algorithm>
#include <numeric>
#include <cmath>
#include <tuple>
#include <map>

SM_UNMANAGED_CODE

namespace
{
	constexpr float PlaneTolerance = 5e-5f;  // faces within 0.05 mm of each other are coplanar
	constexpr float NormalQuant = 1000.0f;    // normals compared to ~0.06 degrees
	constexpr double MinOverlapArea = 1e-7;   // 0.1 mm^2

	using Poly2 = std::vector<glm::dvec2>;

	struct FaceInfo
	{
		glm::vec3 normal;
		float plane;
		double area;
		bool valid;
	};

	struct PlaneKey
	{
		int nx, ny, nz;
		long long d;
		bool operator<(const PlaneKey& o) const
		{
			return std::tie(nx, ny, nz, d) < std::tie(o.nx, o.ny, o.nz, o.d);
		}
	};

	double SignedArea(const Poly2& p)
	{
		double a = 0.0;
		for (std::size_t i = 0; i < p.size(); i++)
		{
			const glm::dvec2& q = p[i];
			const glm::dvec2& r = p[(i + 1) % p.size()];
			a += q.x * r.y - r.x * q.y;
		}
		return 0.5 * a;
	}

	// Sutherland-Hodgman, both polygons convex and counter clockwise
	Poly2 Clip(const Poly2& subject, const Poly2& clipper)
	{
		Poly2 out = subject;
		for (std::size_t i = 0; i < clipper.size() && !out.empty(); i++)
		{
			const glm::dvec2 a = clipper[i];
			const glm::dvec2 b = clipper[(i + 1) % clipper.size()];
			const auto inside = [&](const glm::dvec2& p) { return (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x) >= 0.0; };
			const auto cut = [&](const glm::dvec2& p, const glm::dvec2& q) {
				const glm::dvec2 r = q - p, s = b - a;
				const double den = r.x * s.y - r.y * s.x;
				if (std::abs(den) < 1e-30) return q;
				const double t = ((a.x - p.x) * s.y - (a.y - p.y) * s.x) / den;
				return p + r * t;
			};

			const Poly2 in = std::move(out);
			out.clear();
			glm::dvec2 prev = in.back();
			for (const glm::dvec2& cur : in)
			{
				if (inside(cur))
				{
					if (!inside(prev)) out.push_back(cut(prev, cur));
					out.push_back(cur);
				}
				else if (inside(prev))
				{
					out.push_back(cut(prev, cur));
				}
				prev = cur;
			}
		}
		return out;
	}

	struct UnionFind
	{
		std::vector<std::uint32_t> parent;
		explicit UnionFind(std::size_t n) : parent(n) { std::iota(parent.begin(), parent.end(), 0u); }
		std::uint32_t Find(std::uint32_t a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
		void Join(std::uint32_t a, std::uint32_t b) { a = Find(a); b = Find(b); if (a != b) parent[std::max(a, b)] = std::min(a, b); }
	};
}

CoplanarFixer::Stats CoplanarFixer::Fix(CapturedMesh& mesh, const float offset)
{
	Stats v_stats;
	const std::size_t v_faceCount = mesh.faces.size();
	if (v_faceCount < 2 || offset <= 0.0f) return v_stats;

	// 1. Plane of every face
	std::vector<FaceInfo> v_info(v_faceCount);
	for (std::size_t f = 0; f < v_faceCount; f++)
	{
		const CapturedFace& face = mesh.faces[f];
		glm::dvec3 n(0.0), c(0.0);
		for (std::uint32_t a = 0; a < face.cornerCount; a++)
		{
			const glm::dvec3 p = mesh.corners[face.firstCorner + a].position;
			const glm::dvec3 q = mesh.corners[face.firstCorner + (a + 1) % face.cornerCount].position;
			n += glm::dvec3((p.y - q.y) * (p.z + q.z), (p.z - q.z) * (p.x + q.x), (p.x - q.x) * (p.y + q.y));
			c += p;
		}
		const double len = glm::length(n);
		FaceInfo& info = v_info[f];
		info.valid = len > 1e-12 && face.cornerCount >= 3;
		if (!info.valid) continue;
		n /= len;
		c /= static_cast<double>(face.cornerCount);
		info.normal = glm::vec3(n);
		info.plane = static_cast<float>(glm::dot(n, c));
		info.area = 0.5 * len;
	}

	// 2. Group faces by plane (same facing direction). Neighbouring distance bins are merged.
	std::map<PlaneKey, std::uint32_t> v_planeIds;
	std::vector<std::vector<std::uint32_t>> v_planes;
	for (std::uint32_t f = 0; f < v_faceCount; f++)
	{
		const FaceInfo& info = v_info[f];
		if (!info.valid) continue;
		PlaneKey key{
			static_cast<int>(std::lround(info.normal.x * NormalQuant)),
			static_cast<int>(std::lround(info.normal.y * NormalQuant)),
			static_cast<int>(std::lround(info.normal.z * NormalQuant)),
			std::llround(info.plane / PlaneTolerance) };

		std::uint32_t v_id = 0xFFFFFFFFu;
		for (const long long dd : { 0LL, -1LL, 1LL })
		{
			PlaneKey k = key; k.d += dd;
			const auto it = v_planeIds.find(k);
			if (it != v_planeIds.end()) { v_id = it->second; break; }
		}
		if (v_id == 0xFFFFFFFFu)
		{
			v_id = static_cast<std::uint32_t>(v_planes.size());
			v_planes.emplace_back();
			v_planeIds.emplace(key, v_id);
		}
		v_planes[v_id].push_back(f);
	}

	UnionFind v_groups(v_faceCount);
	std::vector<std::uint8_t> v_overlapping(v_faceCount, 0);

	// 3. Overlap test inside every plane (sweep over x, exact convex clipping of the fan triangles)
	for (const std::vector<std::uint32_t>& v_faces : v_planes)
	{
		if (v_faces.size() < 2) continue;
		bool v_multipleShapes = false;
		for (const std::uint32_t f : v_faces)
			if (mesh.faces[f].shape != mesh.faces[v_faces[0]].shape) { v_multipleShapes = true; break; }
		if (!v_multipleShapes) continue;

		const glm::dvec3 n = v_info[v_faces[0]].normal;
		const glm::dvec3 up = (std::abs(n.z) < 0.9) ? glm::dvec3(0, 0, 1) : glm::dvec3(1, 0, 0);
		const glm::dvec3 u = glm::normalize(glm::cross(n, up));
		const glm::dvec3 v = glm::cross(n, u);

		struct Proj { std::vector<Poly2> tris; glm::dvec2 mn, mx; std::uint32_t face; };
		std::vector<Proj> v_proj;
		v_proj.reserve(v_faces.size());
		for (const std::uint32_t f : v_faces)
		{
			const CapturedFace& face = mesh.faces[f];
			Proj p; p.face = f;
			p.mn = glm::dvec2(std::numeric_limits<double>::max());
			p.mx = glm::dvec2(std::numeric_limits<double>::lowest());
			std::vector<glm::dvec2> pts;
			for (std::uint32_t a = 0; a < face.cornerCount; a++)
			{
				const glm::dvec3 q = mesh.corners[face.firstCorner + a].position;
				const glm::dvec2 q2(glm::dot(q, u), glm::dot(q, v));
				pts.push_back(q2);
				p.mn = glm::min(p.mn, q2);
				p.mx = glm::max(p.mx, q2);
			}
			for (std::size_t a = 1; a + 1 < pts.size(); a++)
			{
				Poly2 tri{ pts[0], pts[a], pts[a + 1] };
				const double sa = SignedArea(tri);
				if (std::abs(sa) < 1e-14) continue;
				if (sa < 0.0) std::swap(tri[1], tri[2]);
				p.tris.push_back(std::move(tri));
			}
			v_proj.push_back(std::move(p));
		}

		std::sort(v_proj.begin(), v_proj.end(), [](const Proj& a, const Proj& b) { return a.mn.x < b.mn.x; });
		std::vector<std::size_t> v_active;
		for (std::size_t i = 0; i < v_proj.size(); i++)
		{
			const Proj& pi = v_proj[i];
			v_active.erase(std::remove_if(v_active.begin(), v_active.end(),
				[&](const std::size_t k) { return v_proj[k].mx.x <= pi.mn.x; }), v_active.end());

			for (const std::size_t k : v_active)
			{
				const Proj& pk = v_proj[k];
				if (mesh.faces[pk.face].shape == mesh.faces[pi.face].shape) continue;
				if (pk.mx.y <= pi.mn.y || pi.mx.y <= pk.mn.y) continue;

				double v_area = 0.0;
				for (const Poly2& a : pi.tris)
					for (const Poly2& b : pk.tris)
					{
						const Poly2 c = Clip(a, b);
						if (c.size() >= 3) v_area += std::abs(SignedArea(c));
					}

				if (v_area > MinOverlapArea)
				{
					v_groups.Join(pi.face, pk.face);
					v_overlapping[pi.face] = v_overlapping[pk.face] = 1;
				}
			}
			v_active.push_back(i);
		}
	}

	// 4. Rank shapes inside every group by their area and push the losers back
	std::unordered_map<std::uint32_t, std::vector<std::uint32_t>> v_members;
	for (std::uint32_t f = 0; f < v_faceCount; f++)
		if (v_overlapping[f]) v_members[v_groups.Find(f)].push_back(f);

	for (const auto& [v_root, v_groupFaces] : v_members)
	{
		std::map<std::uint32_t, double> v_shapeArea;
		for (const std::uint32_t f : v_groupFaces)
			v_shapeArea[mesh.faces[f].shape] += v_info[f].area;
		if (v_shapeArea.size() < 2) continue;

		std::vector<std::pair<std::uint32_t, double>> v_ranked(v_shapeArea.begin(), v_shapeArea.end());
		std::stable_sort(v_ranked.begin(), v_ranked.end(),
			[](const auto& a, const auto& b) { return a.second > b.second; }); // ties keep the lower shape index first

		std::unordered_map<std::uint32_t, std::size_t> v_rank;
		for (std::size_t r = 0; r < v_ranked.size(); r++)
			v_rank[v_ranked[r].first] = r;

		v_stats.groups++;
		for (const std::uint32_t f : v_groupFaces)
		{
			const std::size_t r = v_rank[mesh.faces[f].shape];
			if (r == 0) continue;

			const glm::vec3 v_shift = -v_info[f].normal * (offset * static_cast<float>(r));
			const CapturedFace& face = mesh.faces[f];
			for (std::uint32_t a = 0; a < face.cornerCount; a++)
				mesh.corners[face.firstCorner + a].position += v_shift;

			v_stats.facesMoved++;
			v_stats.maxDepth = std::max(v_stats.maxDepth, r);
		}
	}

	return v_stats;
}

SM_MANAGED_CODE
