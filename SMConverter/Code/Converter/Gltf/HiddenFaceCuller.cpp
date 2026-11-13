#include "HiddenFaceCuller.hpp"

#include <algorithm>
#include <execution>
#include <numeric>
#include <cmath>

SM_UNMANAGED_CODE

namespace
{
	struct Triangle
	{
		glm::vec3 v0, e1, e2;
	};

	struct BvhNode
	{
		glm::vec3 min, max;
		std::uint32_t start, count; // leaf: triangle range
		std::uint32_t left, right;  // inner: children
	};

	class Bvh
	{
	public:
		explicit Bvh(std::vector<Triangle>&& tris)
			: m_tris(std::move(tris))
		{
			m_order.resize(m_tris.size());
			std::iota(m_order.begin(), m_order.end(), 0u);

			m_centroids.resize(m_tris.size());
			for (std::size_t a = 0; a < m_tris.size(); a++)
			{
				const Triangle& t = m_tris[a];
				m_centroids[a] = t.v0 + (t.e1 + t.e2) / 3.0f;
			}

			if (!m_tris.empty())
			{
				m_nodes.reserve(m_tris.size() * 2 / LeafSize + 1);
				Build(0, static_cast<std::uint32_t>(m_tris.size()));
			}
		}

		// Returns true when the ray hits any triangle with t > tMin
		bool Occluded(const glm::vec3& origin, const glm::vec3& dir, const float tMin) const
		{
			if (m_nodes.empty()) return false;

			const glm::vec3 v_invDir(1.0f / dir.x, 1.0f / dir.y, 1.0f / dir.z);

			std::uint32_t v_stack[64];
			int v_sp = 0;
			v_stack[v_sp++] = 0;

			while (v_sp > 0)
			{
				const BvhNode& n = m_nodes[v_stack[--v_sp]];
				if (!HitBox(n, origin, v_invDir)) continue;

				if (n.count > 0)
				{
					for (std::uint32_t a = n.start; a < n.start + n.count; a++)
						if (HitTriangle(m_tris[m_order[a]], origin, dir, tMin))
							return true;
				}
				else if (v_sp < 62)
				{
					v_stack[v_sp++] = n.left;
					v_stack[v_sp++] = n.right;
				}
			}

			return false;
		}

	private:
		static constexpr std::uint32_t LeafSize = 4;

		std::uint32_t Build(const std::uint32_t start, const std::uint32_t count)
		{
			const std::uint32_t v_nodeIdx = static_cast<std::uint32_t>(m_nodes.size());
			m_nodes.emplace_back();

			glm::vec3 v_min(std::numeric_limits<float>::max()), v_max(std::numeric_limits<float>::lowest());
			glm::vec3 v_cMin = v_min, v_cMax = v_max;
			for (std::uint32_t a = start; a < start + count; a++)
			{
				const Triangle& t = m_tris[m_order[a]];
				for (const glm::vec3& p : { t.v0, t.v0 + t.e1, t.v0 + t.e2 })
				{
					v_min = glm::min(v_min, p);
					v_max = glm::max(v_max, p);
				}
				v_cMin = glm::min(v_cMin, m_centroids[m_order[a]]);
				v_cMax = glm::max(v_cMax, m_centroids[m_order[a]]);
			}

			m_nodes[v_nodeIdx].min = v_min;
			m_nodes[v_nodeIdx].max = v_max;

			const glm::vec3 v_extent = v_cMax - v_cMin;
			if (count <= LeafSize || glm::max(v_extent.x, glm::max(v_extent.y, v_extent.z)) <= 0.0f)
			{
				m_nodes[v_nodeIdx].start = start;
				m_nodes[v_nodeIdx].count = count;
				return v_nodeIdx;
			}

			const int v_axis = (v_extent.x > v_extent.y && v_extent.x > v_extent.z) ? 0 : (v_extent.y > v_extent.z ? 1 : 2);
			const std::uint32_t v_mid = start + count / 2;
			std::nth_element(m_order.begin() + start, m_order.begin() + v_mid, m_order.begin() + start + count,
				[this, v_axis](const std::uint32_t a, const std::uint32_t b) {
					return m_centroids[a][v_axis] < m_centroids[b][v_axis];
				});

			const std::uint32_t v_left = Build(start, v_mid - start);
			const std::uint32_t v_right = Build(v_mid, start + count - v_mid);

			m_nodes[v_nodeIdx].count = 0;
			m_nodes[v_nodeIdx].left = v_left;
			m_nodes[v_nodeIdx].right = v_right;
			return v_nodeIdx;
		}

		static bool HitBox(const BvhNode& n, const glm::vec3& o, const glm::vec3& invDir)
		{
			float tMin = 0.0f, tMax = std::numeric_limits<float>::max();
			for (int a = 0; a < 3; a++)
			{
				float t0 = (n.min[a] - o[a]) * invDir[a];
				float t1 = (n.max[a] - o[a]) * invDir[a];
				if (t0 > t1) std::swap(t0, t1);
				tMin = std::max(tMin, t0);
				tMax = std::min(tMax, t1);
				if (tMax < tMin) return false;
			}
			return true;
		}

		// Double sided Moller-Trumbore
		static bool HitTriangle(const Triangle& t, const glm::vec3& o, const glm::vec3& d, const float tMin)
		{
			const glm::vec3 p = glm::cross(d, t.e2);
			const float det = glm::dot(t.e1, p);
			if (std::abs(det) < 1e-12f) return false;

			const float inv = 1.0f / det;
			const glm::vec3 s = o - t.v0;
			const float u = glm::dot(s, p) * inv;
			if (u < 0.0f || u > 1.0f) return false;

			const glm::vec3 q = glm::cross(s, t.e1);
			const float v = glm::dot(d, q) * inv;
			if (v < 0.0f || u + v > 1.0f) return false;

			return glm::dot(t.e2, q) * inv > tMin;
		}

		std::vector<Triangle> m_tris;
		std::vector<std::uint32_t> m_order;
		std::vector<glm::vec3> m_centroids;
		std::vector<BvhNode> m_nodes;
	};

	std::vector<glm::vec3> MakeDirections(const int count)
	{
		std::vector<glm::vec3> v_dirs;
		v_dirs.reserve(count);

		const float v_golden = 3.14159265358979f * (3.0f - std::sqrt(5.0f));
		for (int i = 0; i < count; i++)
		{
			const float y = 1.0f - (static_cast<float>(i) + 0.5f) / static_cast<float>(count) * 2.0f;
			const float r = std::sqrt(std::max(0.0f, 1.0f - y * y));
			const float t = v_golden * static_cast<float>(i);
			v_dirs.emplace_back(std::cos(t) * r, y, std::sin(t) * r);
		}

		return v_dirs;
	}

	// Newell's method, works for n-gons
	glm::vec3 FaceNormal(const CapturedMesh& mesh, const CapturedFace& face)
	{
		glm::vec3 n(0.0f);
		for (std::uint32_t a = 0; a < face.cornerCount; a++)
		{
			const glm::vec3& p = mesh.corners[face.firstCorner + a].position;
			const glm::vec3& q = mesh.corners[face.firstCorner + (a + 1) % face.cornerCount].position;
			n.x += (p.y - q.y) * (p.z + q.z);
			n.y += (p.z - q.z) * (p.x + q.x);
			n.z += (p.x - q.x) * (p.y + q.y);
		}
		return n;
	}
}

namespace
{
	/*
		Outside-in visibility: render triangle ids with a depth buffer from many directions (orthographic,
		software rasterizer). Every face that owns at least one pixel in some view is visible.
		This catches faces that are only partly covered by other parts (a thin visible strip), which the
		inside-out ray test misses when none of its sample points lie in the strip.
	*/
	std::vector<std::uint8_t> RasterVisibility(
		const CapturedMesh& mesh,
		const std::vector<glm::vec3>& dirs,
		const float targetPixelSize,
		const int maxResolution)
	{
		struct RTri { glm::vec3 p0, p1, p2; std::uint32_t face; bool occluder; };

		std::vector<RTri> v_tris;
		v_tris.reserve(mesh.corners.size());
		glm::vec3 v_min(std::numeric_limits<float>::max()), v_max(std::numeric_limits<float>::lowest());
		for (std::uint32_t fIdx = 0; fIdx < mesh.faces.size(); fIdx++)
		{
			const CapturedFace& f = mesh.faces[fIdx];
			const bool v_occluder = !GeometryCapture::Materials[f.material].IsTransparent();
			const glm::vec3& p0 = mesh.corners[f.firstCorner].position;
			for (std::uint32_t a = 1; a + 1 < f.cornerCount; a++)
				v_tris.push_back({ p0, mesh.corners[f.firstCorner + a].position, mesh.corners[f.firstCorner + a + 1].position, fIdx, v_occluder });
			for (std::uint32_t a = 0; a < f.cornerCount; a++)
			{
				v_min = glm::min(v_min, mesh.corners[f.firstCorner + a].position);
				v_max = glm::max(v_max, mesh.corners[f.firstCorner + a].position);
			}
		}

		std::vector<std::uint8_t> v_visible(mesh.faces.size(), 0);
		if (v_tris.empty()) return v_visible;

		const glm::vec3 v_center = (v_min + v_max) * 0.5f;
		const float v_radius = glm::length(v_max - v_min) * 0.5f + 1e-4f;
		const int v_res = std::clamp(static_cast<int>(std::ceil(2.0f * v_radius / targetPixelSize)), 64, maxResolution);
		const float v_pixel = 2.0f * v_radius / static_cast<float>(v_res);
		constexpr std::uint32_t NoFace = 0xFFFFFFFFu;

		std::vector<std::vector<std::uint32_t>> v_perDir(dirs.size());
		std::vector<std::uint32_t> v_dirIdx(dirs.size());
		std::iota(v_dirIdx.begin(), v_dirIdx.end(), 0u);

		std::for_each(std::execution::par, v_dirIdx.begin(), v_dirIdx.end(), [&](const std::uint32_t dIdx) {
			const glm::vec3 d = dirs[dIdx];
			const glm::vec3 v_up = (std::abs(d.z) < 0.9f) ? glm::vec3(0, 0, 1) : glm::vec3(1, 0, 0);
			const glm::vec3 u = glm::normalize(glm::cross(d, v_up));
			const glm::vec3 v = glm::cross(d, u);

			std::vector<float> v_depth(static_cast<std::size_t>(v_res) * v_res, std::numeric_limits<float>::max());
			std::vector<std::uint32_t> v_id(static_cast<std::size_t>(v_res) * v_res, NoFace);
			std::vector<std::uint8_t> v_seen(mesh.faces.size(), 0);

			const auto v_project = [&](const glm::vec3& p) {
				const glm::vec3 q = p - v_center;
				return glm::vec3((glm::dot(q, u) + v_radius) / v_pixel, (glm::dot(q, v) + v_radius) / v_pixel, glm::dot(q, d));
			};

			// pass 0: opaque triangles write depth + id, pass 1: transparent ones are visible where in front
			for (int v_pass = 0; v_pass < 2; v_pass++)
			{
				for (const RTri& t : v_tris)
				{
					if (t.occluder != (v_pass == 0)) continue;

					const glm::vec3 a = v_project(t.p0), b = v_project(t.p1), c = v_project(t.p2);
					const float v_area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
					if (std::abs(v_area) < 1e-12f) continue;
					const float v_invArea = 1.0f / v_area;

					const int x0 = std::max(0, static_cast<int>(std::floor(std::min({ a.x, b.x, c.x }))));
					const int x1 = std::min(v_res - 1, static_cast<int>(std::ceil(std::max({ a.x, b.x, c.x }))));
					const int y0 = std::max(0, static_cast<int>(std::floor(std::min({ a.y, b.y, c.y }))));
					const int y1 = std::min(v_res - 1, static_cast<int>(std::ceil(std::max({ a.y, b.y, c.y }))));

					bool v_visibleHere = false;
					for (int y = y0; y <= y1; y++)
					{
						const float py = static_cast<float>(y) + 0.5f;
						for (int x = x0; x <= x1; x++)
						{
							const float px = static_cast<float>(x) + 0.5f;
							const float w0 = ((b.x - px) * (c.y - py) - (c.x - px) * (b.y - py)) * v_invArea;
							const float w1 = ((c.x - px) * (a.y - py) - (a.x - px) * (c.y - py)) * v_invArea;
							const float w2 = 1.0f - w0 - w1;
							if (w0 < 0.0f || w1 < 0.0f || w2 < 0.0f) continue;

							const float z = w0 * a.z + w1 * b.z + w2 * c.z;
							const std::size_t v_pi = static_cast<std::size_t>(y) * v_res + x;
							if (v_pass == 0)
							{
								if (z < v_depth[v_pi]) { v_depth[v_pi] = z; v_id[v_pi] = t.face; }
							}
							else if (z < v_depth[v_pi])
							{
								v_visibleHere = true;
							}
						}
					}
					if (v_visibleHere) v_seen[t.face] = 1;
				}
			}

			for (const std::uint32_t id : v_id)
				if (id != NoFace) v_seen[id] = 1;

			for (std::uint32_t a = 0; a < v_seen.size(); a++)
				if (v_seen[a]) v_perDir[dIdx].push_back(a);
		});

		for (const auto& v_list : v_perDir)
			for (const std::uint32_t a : v_list)
				v_visible[a] = 1;

		return v_visible;
	}
}

HiddenFaceCuller::Stats HiddenFaceCuller::Cull(CapturedMesh& mesh)
{
	Stats v_stats;
	v_stats.facesIn = mesh.faces.size();
	if (mesh.faces.empty()) return v_stats;

	// Bounds -> offset epsilon
	glm::vec3 v_min(std::numeric_limits<float>::max()), v_max(std::numeric_limits<float>::lowest());
	for (const CapturedCorner& c : mesh.corners)
	{
		v_min = glm::min(v_min, c.position);
		v_max = glm::max(v_max, c.position);
	}
	const glm::vec3 v_extent = v_max - v_min;
	const float v_eps = std::max(1e-6f, std::max(v_extent.x, std::max(v_extent.y, v_extent.z)) * 1e-5f);

	// Occluders: every face that can't be seen through, fan triangulated
	std::vector<Triangle> v_tris;
	v_tris.reserve(mesh.corners.size());
	for (const CapturedFace& f : mesh.faces)
	{
		if (GeometryCapture::Materials[f.material].IsTransparent())
			continue;

		const glm::vec3& p0 = mesh.corners[f.firstCorner].position;
		for (std::uint32_t a = 1; a + 1 < f.cornerCount; a++)
		{
			const glm::vec3& p1 = mesh.corners[f.firstCorner + a].position;
			const glm::vec3& p2 = mesh.corners[f.firstCorner + a + 1].position;
			v_tris.push_back({ p0, p1 - p0, p2 - p0 });
		}
	}

	const Bvh v_bvh(std::move(v_tris));
	static const std::vector<glm::vec3> v_dirs = MakeDirections(96);

	std::vector<std::uint8_t> v_visible(mesh.faces.size(), 1);
	std::vector<std::uint32_t> v_faceIdx(mesh.faces.size());
	std::iota(v_faceIdx.begin(), v_faceIdx.end(), 0u);

	std::for_each(std::execution::par, v_faceIdx.begin(), v_faceIdx.end(),
		[&](const std::uint32_t fIdx) {
			const CapturedFace& f = mesh.faces[fIdx];

			glm::vec3 n = FaceNormal(mesh, f);
			const float v_len = glm::length(n);
			if (v_len < 1e-12f) return; // degenerate, keep
			n /= v_len;

			glm::vec3 c(0.0f);
			for (std::uint32_t a = 0; a < f.cornerCount; a++)
				c += mesh.corners[f.firstCorner + a].position;
			c /= static_cast<float>(f.cornerCount);

			// Sample points: centre + towards every corner (max 8 corners sampled)
			glm::vec3 v_points[9];
			std::uint32_t v_pointCount = 0;
			v_points[v_pointCount++] = c;
			const std::uint32_t v_step = std::max(1u, f.cornerCount / 8);
			for (std::uint32_t a = 0; a < f.cornerCount && v_pointCount < 9; a += v_step)
				v_points[v_pointCount++] = glm::mix(c, mesh.corners[f.firstCorner + a].position, 0.8f);

			for (const float v_side : { 1.0f, -1.0f })
			{
				const glm::vec3 v_sideNormal = n * v_side;
				for (std::uint32_t p = 0; p < v_pointCount; p++)
				{
					const glm::vec3 v_origin = v_points[p] + v_sideNormal * v_eps;
					for (const glm::vec3& d : v_dirs)
					{
						if (glm::dot(d, v_sideNormal) <= 0.05f) continue;
						if (!v_bvh.Occluded(v_origin, d, 0.0f))
							return; // escapes -> visible
					}
				}
			}

			v_visible[fIdx] = 0;
		});

	// Outside-in pass: keep every face that shows up in a rendered view (partly covered faces)
	{
		static const std::vector<glm::vec3> v_viewDirs = MakeDirections(256);
		// ~3 mm pixels, at most 2048 x 2048 per view
		const std::vector<std::uint8_t> v_rasterVisible = RasterVisibility(mesh, v_viewDirs, 0.003f, 2048);
		for (std::size_t a = 0; a < v_visible.size(); a++)
		{
			if (!v_visible[a] && v_rasterVisible[a])
			{
				v_visible[a] = 1;
				v_stats.keptByRaster++;
			}
		}
	}

	// Compact
	CapturedMesh v_out;
	v_out.name = std::move(mesh.name);
	v_out.faces.reserve(mesh.faces.size());
	v_out.corners.reserve(mesh.corners.size());

	for (std::size_t a = 0; a < mesh.faces.size(); a++)
	{
		if (!v_visible[a]) continue;

		CapturedFace f = mesh.faces[a];
		const std::uint32_t v_first = f.firstCorner;
		f.firstCorner = static_cast<std::uint32_t>(v_out.corners.size());
		v_out.corners.insert(v_out.corners.end(),
			mesh.corners.begin() + v_first,
			mesh.corners.begin() + v_first + f.cornerCount);
		v_out.faces.push_back(f);
	}

	mesh = std::move(v_out);
	v_stats.facesOut = mesh.faces.size();
	return v_stats;
}

SM_MANAGED_CODE
