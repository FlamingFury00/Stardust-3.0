#include "arena_mesh.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace stardust_sim {
namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kUuToBullet = 1.0 / 50.0;

// Standard Soccar dimensions (uu). Values marked "measured" come from published measurements of the
// game's collision mesh; the rest are the documented RLBot/RocketSim arena constants.
constexpr double kExtentX = 4096.0;
constexpr double kExtentY = 5120.0;
constexpr double kHeight = 2044.0;
constexpr double kCornerPlane = 8064.0;         // |x| + |y| on the flat 45 degree corner walls
constexpr double kCornerBlendSide = 680.0;      // measured fillet: corner wall -> side wall
constexpr double kCornerBlendBack = 800.0;      // measured fillet: corner wall -> back wall
constexpr double kSideRampRadius = 256.0;       // measured floor ramp under side and corner walls
constexpr double kBackRampRadius = 160.0;       // measured floor ramp under the back walls
constexpr double kCeilingRadius = 550.0;        // measured wall-to-ceiling curve
constexpr double kGoalHalfWidth = 893.0;
constexpr double kGoalHeight = 642.775;
constexpr double kGoalDepth = 880.0;
constexpr double kGoalBackCurveRadius = 256.0;  // measured quarter-pipe at the back of the net
constexpr double kGoalBackCurveDepth = 624.0;   // centre of that quarter-pipe behind the goal line
constexpr double kGoalRoofBackDepth = 733.0;    // measured top of the back curve
constexpr double kGoalRoofBackHeight = 477.0;
constexpr double kGoalRoofFrontDepth = 224.0;   // roof meets the flat lintel here
constexpr double kMinMiter = 0.5;               // cap the miter stretch at 2x (turns under 120 degrees)
constexpr int kCornerArcSegments = 14;
constexpr int kFloorRampSegments = 16;
constexpr int kCeilingSegments = 16;
constexpr int kGoalCurveSegments = 12;

struct Point2 {
    double x, y;
};

Point2 operator+(Point2 a, Point2 b) { return {a.x + b.x, a.y + b.y}; }
Point2 operator-(Point2 a, Point2 b) { return {a.x - b.x, a.y - b.y}; }
Point2 operator*(Point2 a, double s) { return {a.x * s, a.y * s}; }

double CircularInset(double radius, double height) {
    if (height >= radius) return 0.0;
    double d = radius - height;
    return radius - std::sqrt(std::max(0.0, radius * radius - d * d));
}

double CeilingInset(double z) { return CircularInset(kCeilingRadius, kHeight - z); }

std::vector<Point2> Arc(Point2 from, Point2 to, Point2 centre) {
    double a0 = std::atan2(from.y - centre.y, from.x - centre.x);
    double a1 = std::atan2(to.y - centre.y, to.x - centre.x);
    while (a1 - a0 > kPi) a1 -= 2 * kPi;
    while (a1 - a0 < -kPi) a1 += 2 * kPi;
    double radius = std::hypot(from.x - centre.x, from.y - centre.y);
    std::vector<Point2> points;
    for (int i = 0; i <= kCornerArcSegments; i++) {
        double angle = a0 + (a1 - a0) * i / kCornerArcSegments;
        points.push_back({centre.x + radius * std::cos(angle), centre.y + radius * std::sin(angle)});
    }
    return points;
}

/// One quadrant corner, from the side-wall tangent point to the back-wall tangent point, in the
/// (+x, +y) frame and mirrored by (sx, sy).
std::vector<Point2> CornerPath(double sx, double sy) {
    const double tanHalf = std::tan(kPi / 8);
    const double diag = std::sqrt(0.5);
    const double dSide = kCornerBlendSide * tanHalf;
    const double dBack = kCornerBlendBack * tanHalf;
    const double sideVertexY = kCornerPlane - kExtentX;  // diagonal meets side wall
    const double backVertexX = kCornerPlane - kExtentY;  // diagonal meets back wall

    Point2 sideTangent{kExtentX, sideVertexY - dSide};
    Point2 diagonalFromSide{kExtentX - dSide * diag, sideVertexY + dSide * diag};
    Point2 sideCentre{kExtentX - kCornerBlendSide, sideVertexY - dSide};
    Point2 diagonalToBack{backVertexX + dBack * diag, kExtentY - dBack * diag};
    Point2 backTangent{backVertexX - dBack, kExtentY};
    Point2 backCentre{backVertexX - dBack, kExtentY - kCornerBlendBack};

    std::vector<Point2> path = Arc(sideTangent, diagonalFromSide, sideCentre);
    std::vector<Point2> back = Arc(diagonalToBack, backTangent, backCentre);
    path.insert(path.end(), back.begin(), back.end());
    for (Point2& p : path) p = {p.x * sx, p.y * sy};
    return path;
}

struct Ring {
    std::vector<int32_t> vertices;
};

void BuildShell(TriangleMesh& mesh) {
    // Counter-clockwise outline seen from above: up the +x wall, across +y toward -x, down the -x
    // wall, across -y toward +x. Straight side and back walls are the implied closing edges.
    std::vector<Point2> outline;
    auto append = [&](std::vector<Point2> part, bool reverse) {
        if (reverse) std::reverse(part.begin(), part.end());
        outline.insert(outline.end(), part.begin(), part.end());
    };
    append(CornerPath(1, 1), false);
    append(CornerPath(-1, 1), true);
    append(CornerPath(-1, -1), false);
    append(CornerPath(1, -1), true);

    const size_t edgeCount = outline.size();
    std::vector<bool> backEdge(edgeCount);
    std::vector<Point2> inward(edgeCount);
    for (size_t i = 0; i < edgeCount; i++) {
        Point2 a = outline[i], b = outline[(i + 1) % edgeCount];
        backEdge[i] = std::abs(std::abs(a.y) - kExtentY) < 1e-6 && std::abs(std::abs(b.y) - kExtentY) < 1e-6;
        Point2 d = b - a;
        double length = std::hypot(d.x, d.y);
        inward[i] = {-d.y / length, d.x / length};
    }

    auto edgeInset = [&](size_t edge, double z) {
        double ramp = backEdge[edge] ? kBackRampRadius : kSideRampRadius;
        return CircularInset(ramp, z) + CeilingInset(z);
    };

    // Vertex i moves inward along the bisector of its two edges by its own inset, stretched by the
    // miter factor so both edges stay offset by that inset. The floor ramp narrows from the side
    // and corner walls to the back wall across the corner fillet that turns onto the back wall.
    // (Intersecting the neighbouring edges' offset lines instead fails there: the fillet meets
    // the back wall almost tangentially, and two nearly parallel lines with different insets
    // cross thousands of units along the wall, which once put ramp triangles in the goal mouth.)
    auto insetVertex = [&](size_t i, double z) -> Point2 {
        size_t previous = (i + edgeCount - 1) % edgeCount;
        Point2 n1 = inward[previous], n2 = inward[i];
        Point2 bisector = n1 + n2;
        double length = std::hypot(bisector.x, bisector.y);
        bisector = length > 1e-9 ? bisector * (1.0 / length) : n2;
        // cos of half the turn at the vertex; the outline only turns gently, the floor guards a spike.
        double miter = std::max(kMinMiter, bisector.x * n2.x + bisector.y * n2.y);
        const double diagonal = std::sqrt(0.5);
        double backward = std::clamp((std::abs(bisector.y) - diagonal) / (1.0 - diagonal), 0.0, 1.0);
        double ramp = kSideRampRadius + (kBackRampRadius - kSideRampRadius) * backward;
        double inset = CircularInset(ramp, z) + CeilingInset(z);
        return outline[i] + bisector * (inset / miter);
    };

    // Ring heights: dense floor-ramp samples, the crossbar height, and the ceiling curve.
    std::vector<double> heights;
    for (int k = 0; k <= kFloorRampSegments; k++)
        heights.push_back(kSideRampRadius * (1 - std::cos(0.5 * kPi * k / kFloorRampSegments)));
    heights.push_back(kBackRampRadius);
    heights.push_back(kGoalHeight);
    for (int k = 0; k <= kCeilingSegments; k++)
        heights.push_back(kHeight - kCeilingRadius + kCeilingRadius * std::sin(0.5 * kPi * k / kCeilingSegments));
    std::sort(heights.begin(), heights.end());
    heights.erase(std::unique(heights.begin(), heights.end(),
                      [](double a, double b) { return std::abs(a - b) < 1e-6; }),
        heights.end());

    std::vector<Ring> rings;
    std::vector<bool> mouth;
    for (double z : heights) {
        Ring ring;
        mouth.clear();
        for (size_t i = 0; i < edgeCount; i++) {
            Point2 v = insetVertex(i, z);
            ring.vertices.push_back(mesh.AddVertex(v.x, v.y, z));
            Point2 a = outline[i], b = outline[(i + 1) % edgeCount];
            bool spansMouth = backEdge[i] && std::min(a.x, b.x) < -kGoalHalfWidth && std::max(a.x, b.x) > kGoalHalfWidth;
            if (!spansMouth) {
                mouth.push_back(false);
                continue;
            }
            double y = (a.y > 0 ? 1 : -1) * (kExtentY - edgeInset(i, z));
            double direction = b.x > a.x ? 1 : -1;
            mouth.push_back(false);
            ring.vertices.push_back(mesh.AddVertex(-direction * kGoalHalfWidth, y, z));
            mouth.push_back(true);
            ring.vertices.push_back(mesh.AddVertex(direction * kGoalHalfWidth, y, z));
            mouth.push_back(false);
        }
        rings.push_back(ring);
    }

    const double cx = 0, cy = 0, cz = kHeight * 0.5;
    const size_t ringSize = rings[0].vertices.size();
    for (size_t k = 0; k + 1 < rings.size(); k++) {
        const Ring& below = rings[k];
        const Ring& above = rings[k + 1];
        bool bandBelowCrossbar = heights[k + 1] <= kGoalHeight + 1e-6;
        for (size_t j = 0; j < ringSize; j++) {
            if (mouth[j] && bandBelowCrossbar) continue;
            size_t j1 = (j + 1) % ringSize;
            mesh.AddTriangleFacing(below.vertices[j], above.vertices[j], below.vertices[j1], cx, cy, cz);
            mesh.AddTriangleFacing(below.vertices[j1], above.vertices[j], above.vertices[j1], cx, cy, cz);
        }
    }

    // Flat ceiling inside the rounded top ring.
    int32_t centre = mesh.AddVertex(0, 0, kHeight);
    const Ring& top = rings.back();
    for (size_t j = 0; j < ringSize; j++)
        mesh.AddTriangleFacing(centre, top.vertices[j], top.vertices[(j + 1) % ringSize], cx, cy, cz);

    // Close the open ends of each back-wall floor ramp at the goal posts.
    for (double sy : {-1.0, 1.0}) {
        for (double sx : {-1.0, 1.0}) {
            double x = sx * kGoalHalfWidth;
            int32_t corner = mesh.AddVertex(x, sy * kExtentY, 0);
            std::vector<int32_t> profile;
            for (double z : heights) {
                if (z > kBackRampRadius + 1e-6) break;
                profile.push_back(mesh.AddVertex(x, sy * (kExtentY - CircularInset(kBackRampRadius, z)), z));
            }
            for (size_t k = 0; k + 1 < profile.size(); k++)
                mesh.AddTriangleFacing(corner, profile[k], profile[k + 1], 0, sy * kExtentY, kGoalHeight * 0.5);
        }
    }
}

void BuildGoal(TriangleMesh& mesh, double side) {
    // Chamber profile as (depth behind the goal line, height): floor, a quarter-pipe back curling
    // past vertical, a roof rising toward the crossbar, and the flat lintel out to the mouth.
    std::vector<Point2> profile;
    profile.push_back({0, 0});
    profile.push_back({kGoalBackCurveDepth, 0});
    const double endAngle = std::atan2(kGoalRoofBackHeight - kGoalBackCurveRadius,
                                       kGoalRoofBackDepth - kGoalBackCurveDepth);
    for (int k = 1; k <= kGoalCurveSegments; k++) {
        double angle = -0.5 * kPi + (0.5 * kPi + endAngle) * k / kGoalCurveSegments;
        profile.push_back({kGoalBackCurveDepth + kGoalBackCurveRadius * std::cos(angle),
                           kGoalBackCurveRadius + kGoalBackCurveRadius * std::sin(angle)});
    }
    profile.push_back({kGoalRoofFrontDepth, kGoalHeight});
    profile.push_back({0, kGoalHeight});

    const size_t count = profile.size();
    const double rx = 0, ry = side * (kExtentY + kGoalDepth * 0.45), rz = kGoalHeight * 0.5;
    std::vector<int32_t> left, right;
    for (const Point2& p : profile) {
        left.push_back(mesh.AddVertex(-kGoalHalfWidth, side * (kExtentY + p.x), p.y));
        right.push_back(mesh.AddVertex(kGoalHalfWidth, side * (kExtentY + p.x), p.y));
    }
    // Segment 0 is the floor (a RocketSim plane) and the closing segment is the open mouth.
    for (size_t k = 1; k + 1 < count; k++) {
        mesh.AddTriangleFacing(left[k], right[k], right[k + 1], rx, ry, rz);
        mesh.AddTriangleFacing(left[k], right[k + 1], left[k + 1], rx, ry, rz);
    }

    Point2 centroid{0, 0};
    for (const Point2& p : profile) centroid = centroid + p;
    centroid = centroid * (1.0 / count);
    for (double x : {-kGoalHalfWidth, kGoalHalfWidth}) {
        const std::vector<int32_t>& rail = x < 0 ? left : right;
        int32_t hub = mesh.AddVertex(x, side * (kExtentY + centroid.x), centroid.y);
        for (size_t k = 0; k < count; k++)
            mesh.AddTriangleFacing(hub, rail[k], rail[(k + 1) % count], rx, ry, rz);
    }
}

}  // namespace

int32_t TriangleMesh::AddVertex(double x, double y, double z) {
    vertices.push_back(static_cast<float>(x));
    vertices.push_back(static_cast<float>(y));
    vertices.push_back(static_cast<float>(z));
    return VertexCount() - 1;
}

void TriangleMesh::AddTriangle(int32_t a, int32_t b, int32_t c) {
    indices.push_back(a);
    indices.push_back(b);
    indices.push_back(c);
}

void TriangleMesh::AddTriangleFacing(int32_t a, int32_t b, int32_t c, double rx, double ry, double rz) {
    const float* pa = &vertices[a * 3];
    const float* pb = &vertices[b * 3];
    const float* pc = &vertices[c * 3];
    double ux = pb[0] - pa[0], uy = pb[1] - pa[1], uz = pb[2] - pa[2];
    double vx = pc[0] - pa[0], vy = pc[1] - pa[1], vz = pc[2] - pa[2];
    double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
    if (nx * nx + ny * ny + nz * nz < 1e-9) return;  // degenerate sliver
    double facing = nx * (rx - pa[0]) + ny * (ry - pa[1]) + nz * (rz - pa[2]);
    if (facing >= 0)
        AddTriangle(a, b, c);
    else
        AddTriangle(a, c, b);
}

std::vector<uint8_t> TriangleMesh::ToCollisionMeshFile() const {
    const int32_t triangleCount = TriangleCount();
    const int32_t vertexCount = VertexCount();
    std::vector<uint8_t> bytes(8 + indices.size() * 4 + vertices.size() * 4);
    uint8_t* cursor = bytes.data();
    std::memcpy(cursor, &triangleCount, 4);
    std::memcpy(cursor + 4, &vertexCount, 4);
    cursor += 8;
    std::memcpy(cursor, indices.data(), indices.size() * 4);
    cursor += indices.size() * 4;
    for (float v : vertices) {
        float scaled = static_cast<float>(v * kUuToBullet);
        std::memcpy(cursor, &scaled, 4);
        cursor += 4;
    }
    return bytes;
}

SoccarArenaMeshes BuildSoccarArena() {
    SoccarArenaMeshes meshes;
    BuildShell(meshes.shell);
    BuildGoal(meshes.blueGoal, -1);
    BuildGoal(meshes.orangeGoal, 1);
    return meshes;
}

}  // namespace stardust_sim
