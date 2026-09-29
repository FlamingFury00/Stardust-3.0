#pragma once

#include <cstdint>
#include <vector>

namespace stardust_sim {

/// Triangle mesh in Rocket League unreal units (uu). Triangle normals point into the playable volume.
struct TriangleMesh {
    std::vector<float> vertices;   // x, y, z triplets
    std::vector<int32_t> indices;  // vertex index triplets

    int32_t AddVertex(double x, double y, double z);
    void AddTriangle(int32_t a, int32_t b, int32_t c);

    /// Adds a triangle whose normal faces the supplied interior reference point.
    void AddTriangleFacing(int32_t a, int32_t b, int32_t c, double rx, double ry, double rz);

    int32_t TriangleCount() const { return static_cast<int32_t>(indices.size() / 3); }
    int32_t VertexCount() const { return static_cast<int32_t>(vertices.size() / 3); }

    /// Serializes to RocketSim's collision-mesh-file layout: counts, triangles, then vertices in
    /// Bullet units (uu / 50).
    std::vector<uint8_t> ToCollisionMeshFile() const;
};

/// Analytic standard Soccar arena fitted to published collision-mesh measurements.
/// RocketSim adds the floor, ceiling and side-wall planes itself; these meshes supply the
/// rounded shell (ramps, corners, back walls, ceiling curve) and both goal chambers.
struct SoccarArenaMeshes {
    TriangleMesh shell;
    TriangleMesh blueGoal;
    TriangleMesh orangeGoal;
};

SoccarArenaMeshes BuildSoccarArena();

}  // namespace stardust_sim
