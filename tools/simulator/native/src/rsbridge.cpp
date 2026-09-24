// Flat C interface over RocketSim for the Stardust match simulator (P/Invoke friendly).
// All vectors are world-space uu; every struct is plain data mirrored exactly in C#.

#include <cstdint>
#include <map>
#include <memory>
#include <mutex>
#include <vector>

#include "RocketSim.h"
#include "Sim/BallPredTracker/BallPredTracker.h"
#include "arena_mesh.hpp"

#if defined(_WIN32)
#define RSB_EXPORT extern "C" __declspec(dllexport)
#else
#define RSB_EXPORT extern "C" __attribute__((visibility("default")))
#endif

using namespace RocketSim;

struct RsbVec {
    float x, y, z;
};

struct RsbPhysics {
    RsbVec position;
    RsbVec forward, right, up;
    RsbVec velocity;
    RsbVec angularVelocity;
};

struct RsbControls {
    float throttle, steer, pitch, yaw, roll;
    int32_t jump, boost, handbrake;
};

struct RsbCarState {
    uint64_t lastHitTick;  // arena tick of the latest ball contact, UINT64_MAX when none
    RsbPhysics physics;
    RsbControls lastControls;
    RsbVec lastHitBallPosition;
    RsbVec lastHitRelativePosition;
    RsbVec worldContactNormal;
    uint32_t id;
    int32_t team;
    int32_t isOnGround, hasJumped, hasDoubleJumped, hasFlipped;
    int32_t isJumping, isFlipping, isSupersonic, isDemoed, isBoosting, hasWorldContact;
    int32_t wheelContactMask;
    float boost, jumpTime, flipTime, airTime, airTimeSinceJump, demoRespawnTimer;
    float handbrakeValue, supersonicTime, timeSinceBoosted;
};

struct RsbBallState {
    RsbPhysics physics;
};

struct RsbPad {
    RsbVec position;
    int32_t isBig, isActive;
    float cooldown;
};

struct RsbEvents {
    int32_t goalTeam;      // -1 none, 0 blue scored, 1 orange scored
    int32_t demolitions;   // demolitions since the last poll
    int32_t bumps;         // non-demolition car bumps since the last poll
};

struct RsbDemo {
    uint32_t bumper, victim;
};

namespace {

std::once_flag gInitFlag;
int gInitResult = -1;

struct ArenaHandle {
    Arena* arena = nullptr;
    std::unique_ptr<BallPredTracker> prediction;
    size_t predictionTicks = 0;
    RsbEvents events{-1, 0, 0};
    std::vector<RsbDemo> demos;
};

RsbVec ToRsb(const Vec& v) { return {v.x, v.y, v.z}; }
Vec FromRsb(const RsbVec& v) { return Vec(v.x, v.y, v.z); }

RsbPhysics ToRsb(const PhysState& s) {
    RsbPhysics p;
    p.position = ToRsb(s.pos);
    p.forward = ToRsb(s.rotMat.forward);
    p.right = ToRsb(s.rotMat.right);
    p.up = ToRsb(s.rotMat.up);
    p.velocity = ToRsb(s.vel);
    p.angularVelocity = ToRsb(s.angVel);
    return p;
}

void FromRsb(const RsbPhysics& p, PhysState& s) {
    s.pos = FromRsb(p.position);
    s.rotMat = RotMat(FromRsb(p.forward), FromRsb(p.right), FromRsb(p.up));
    s.vel = FromRsb(p.velocity);
    s.angVel = FromRsb(p.angularVelocity);
}

RsbControls ToRsb(const CarControls& c) {
    return {c.throttle, c.steer, c.pitch, c.yaw, c.roll, c.jump, c.boost, c.handbrake};
}

CarControls FromRsb(const RsbControls& c) {
    CarControls result;
    result.throttle = c.throttle;
    result.steer = c.steer;
    result.pitch = c.pitch;
    result.yaw = c.yaw;
    result.roll = c.roll;
    result.jump = c.jump != 0;
    result.boost = c.boost != 0;
    result.handbrake = c.handbrake != 0;
    result.ClampFix();
    return result;
}

void FillCar(Car* car, RsbCarState* out) {
    CarState s = car->GetState();
    out->lastHitTick = s.ballHitInfo.isValid ? s.ballHitInfo.tickCountWhenHit : UINT64_MAX;
    out->physics = ToRsb(s);
    out->lastControls = ToRsb(s.lastControls);
    out->lastHitBallPosition = ToRsb(s.ballHitInfo.ballPos);
    out->lastHitRelativePosition = ToRsb(s.ballHitInfo.relativePosOnBall);
    out->worldContactNormal = ToRsb(s.worldContact.contactNormal);
    out->id = car->id;
    out->team = static_cast<int32_t>(car->team);
    out->isOnGround = s.isOnGround;
    out->hasJumped = s.hasJumped;
    out->hasDoubleJumped = s.hasDoubleJumped;
    out->hasFlipped = s.hasFlipped;
    out->isJumping = s.isJumping;
    out->isFlipping = s.isFlipping;
    out->isSupersonic = s.isSupersonic;
    out->isDemoed = s.isDemoed;
    out->isBoosting = s.isBoosting;
    out->hasWorldContact = s.worldContact.hasContact;
    int mask = 0;
    for (int i = 0; i < 4; i++)
        if (s.wheelsWithContact[i]) mask |= 1 << i;
    out->wheelContactMask = mask;
    out->boost = s.boost;
    out->jumpTime = s.jumpTime;
    out->flipTime = s.flipTime;
    out->airTime = s.airTime;
    out->airTimeSinceJump = s.airTimeSinceJump;
    out->demoRespawnTimer = s.demoRespawnTimer;
    out->handbrakeValue = s.handbrakeVal;
    out->supersonicTime = s.supersonicTime;
    out->timeSinceBoosted = s.timeSinceBoosted;
}

const CarConfig& ConfigFor(int32_t preset) {
    switch (preset) {
        case 1: return CAR_CONFIG_DOMINUS;
        case 2: return CAR_CONFIG_PLANK;
        case 3: return CAR_CONFIG_BREAKOUT;
        case 4: return CAR_CONFIG_HYBRID;
        case 5: return CAR_CONFIG_MERC;
        default: return CAR_CONFIG_OCTANE;
    }
}

}  // namespace

RSB_EXPORT int32_t rsb_init() {
    std::call_once(gInitFlag, [] {
        stardust_sim::SoccarArenaMeshes meshes = stardust_sim::BuildSoccarArena();
        std::map<GameMode, std::vector<FileData>> files;
        for (const stardust_sim::TriangleMesh* mesh : {&meshes.shell, &meshes.blueGoal, &meshes.orangeGoal})
            files[GameMode::SOCCAR].push_back(mesh->ToCollisionMeshFile());
        RocketSim::InitFromMem(files, true);
        gInitResult = 0;
    });
    return gInitResult;
}

RSB_EXPORT int32_t rsb_mesh_stats(int32_t* triangles, int32_t* vertices) {
    stardust_sim::SoccarArenaMeshes meshes = stardust_sim::BuildSoccarArena();
    *triangles = meshes.shell.TriangleCount() + meshes.blueGoal.TriangleCount() + meshes.orangeGoal.TriangleCount();
    *vertices = meshes.shell.VertexCount() + meshes.blueGoal.VertexCount() + meshes.orangeGoal.VertexCount();
    return 0;
}

RSB_EXPORT void* rsb_arena_create(float tickRate) {
    if (rsb_init() != 0) return nullptr;
    auto* handle = new ArenaHandle();
    handle->arena = Arena::Create(GameMode::SOCCAR, ArenaConfig(), tickRate);
    handle->arena->SetGoalScoreCallback(
        [](Arena*, Team scoringTeam, void* user) {
            auto* h = static_cast<ArenaHandle*>(user);
            if (h->events.goalTeam < 0) h->events.goalTeam = static_cast<int32_t>(scoringTeam);
        },
        handle);
    handle->arena->SetCarBumpCallback(
        [](Arena*, Car* bumper, Car* victim, bool isDemo, void* user) {
            auto* h = static_cast<ArenaHandle*>(user);
            if (isDemo) {
                h->events.demolitions++;
                h->demos.push_back({bumper->id, victim->id});
            } else {
                h->events.bumps++;
            }
        },
        handle);
    return handle;
}

RSB_EXPORT void rsb_arena_destroy(void* arenaHandle) {
    auto* handle = static_cast<ArenaHandle*>(arenaHandle);
    if (!handle) return;
    handle->prediction.reset();
    delete handle->arena;
    delete handle;
}

RSB_EXPORT uint32_t rsb_add_car(void* arenaHandle, int32_t team, int32_t preset) {
    auto* handle = static_cast<ArenaHandle*>(arenaHandle);
    Car* car = handle->arena->AddCar(team == 0 ? Team::BLUE : Team::ORANGE, ConfigFor(preset));
    return car->id;
}

RSB_EXPORT void rsb_car_hitbox(void* arenaHandle, uint32_t id, RsbVec* size, RsbVec* offset) {
    auto* handle = static_cast<ArenaHandle*>(arenaHandle);
    Car* car = handle->arena->GetCar(id);
    if (!car) return;
    *size = ToRsb(car->config.hitboxSize);
    *offset = ToRsb(car->config.hitboxPosOffset);
}

RSB_EXPORT void rsb_reset_kickoff(void* arenaHandle, int32_t seed) {
    auto* handle = static_cast<ArenaHandle*>(arenaHandle);
    handle->arena->ResetToRandomKickoff(seed);
    handle->events = {-1, 0, 0};
    handle->demos.clear();
}

RSB_EXPORT void rsb_step(void* arenaHandle, int32_t ticks) {
    static_cast<ArenaHandle*>(arenaHandle)->arena->Step(ticks);
}

RSB_EXPORT uint64_t rsb_tick_count(void* arenaHandle) {
    return static_cast<ArenaHandle*>(arenaHandle)->arena->tickCount;
}

RSB_EXPORT float rsb_tick_time(void* arenaHandle) {
    return static_cast<ArenaHandle*>(arenaHandle)->arena->tickTime;
}

RSB_EXPORT void rsb_get_ball(void* arenaHandle, RsbBallState* out) {
    out->physics = ToRsb(static_cast<ArenaHandle*>(arenaHandle)->arena->ball->GetState());
}

RSB_EXPORT void rsb_set_ball(void* arenaHandle, const RsbBallState* state) {
    auto* handle = static_cast<ArenaHandle*>(arenaHandle);
    BallState s = handle->arena->ball->GetState();
    FromRsb(state->physics, s);
    handle->arena->ball->SetState(s);
}

RSB_EXPORT int32_t rsb_get_car(void* arenaHandle, uint32_t id, RsbCarState* out) {
    Car* car = static_cast<ArenaHandle*>(arenaHandle)->arena->GetCar(id);
    if (!car) return -1;
    FillCar(car, out);
    return 0;
}

/// Sets kinematics, boost and jump flags. Persistent flags are applied as given so fixtures can
/// start airborne with a spent or available flip.
RSB_EXPORT int32_t rsb_set_car(void* arenaHandle, uint32_t id, const RsbCarState* state) {
    Car* car = static_cast<ArenaHandle*>(arenaHandle)->arena->GetCar(id);
    if (!car) return -1;
    CarState s = car->GetState();
    FromRsb(state->physics, s);
    s.boost = state->boost;
    s.isOnGround = state->isOnGround != 0;
    s.hasJumped = state->hasJumped != 0;
    s.hasDoubleJumped = state->hasDoubleJumped != 0;
    s.hasFlipped = state->hasFlipped != 0;
    s.isJumping = false;
    s.isFlipping = false;
    s.jumpTime = 0;
    s.flipTime = 0;
    s.airTime = state->airTime;
    s.airTimeSinceJump = state->airTimeSinceJump;
    s.isDemoed = false;
    s.demoRespawnTimer = 0;
    car->SetState(s);
    return 0;
}

RSB_EXPORT int32_t rsb_set_controls(void* arenaHandle, uint32_t id, const RsbControls* controls) {
    Car* car = static_cast<ArenaHandle*>(arenaHandle)->arena->GetCar(id);
    if (!car) return -1;
    car->controls = FromRsb(*controls);
    return 0;
}

RSB_EXPORT int32_t rsb_pad_count(void* arenaHandle) {
    return static_cast<int32_t>(static_cast<ArenaHandle*>(arenaHandle)->arena->GetBoostPads().size());
}

RSB_EXPORT void rsb_get_pads(void* arenaHandle, RsbPad* out, int32_t capacity) {
    const auto& pads = static_cast<ArenaHandle*>(arenaHandle)->arena->GetBoostPads();
    for (int32_t i = 0; i < capacity && i < static_cast<int32_t>(pads.size()); i++) {
        BoostPadState s = pads[i]->GetState();
        out[i] = {ToRsb(pads[i]->config.pos), pads[i]->config.isBig, s.isActive, s.cooldown};
    }
}

RSB_EXPORT void rsb_set_pad(void* arenaHandle, int32_t index, int32_t active, float cooldown) {
    const auto& pads = static_cast<ArenaHandle*>(arenaHandle)->arena->GetBoostPads();
    if (index < 0 || index >= static_cast<int32_t>(pads.size())) return;
    BoostPadState s = pads[index]->GetState();
    s.isActive = active != 0;
    s.cooldown = cooldown;
    pads[index]->SetState(s);
}

/// Returns and clears the events accumulated since the previous poll.
RSB_EXPORT void rsb_poll_events(void* arenaHandle, RsbEvents* out, RsbDemo* demos, int32_t demoCapacity,
                                int32_t* demoCount) {
    auto* handle = static_cast<ArenaHandle*>(arenaHandle);
    *out = handle->events;
    int32_t n = 0;
    for (const RsbDemo& demo : handle->demos) {
        if (n >= demoCapacity) break;
        demos[n++] = demo;
    }
    *demoCount = n;
    handle->events = {-1, 0, 0};
    handle->demos.clear();
}

RSB_EXPORT int32_t rsb_is_ball_scored(void* arenaHandle) {
    return static_cast<ArenaHandle*>(arenaHandle)->arena->IsBallScored() ? 1 : 0;
}

/// Ball-only prediction from the current arena state. Incremental: unchanged trajectories are
/// shifted rather than re-simulated. Writes up to capacity states, one per tick starting next tick.
RSB_EXPORT int32_t rsb_predict_ball(void* arenaHandle, int32_t ticks, RsbBallState* out, int32_t capacity) {
    auto* handle = static_cast<ArenaHandle*>(arenaHandle);
    if (!handle->prediction || handle->predictionTicks != static_cast<size_t>(ticks)) {
        handle->prediction = std::make_unique<BallPredTracker>(handle->arena, static_cast<size_t>(ticks));
        handle->predictionTicks = static_cast<size_t>(ticks);
    }
    handle->prediction->UpdatePredFromArena(handle->arena);
    const auto& data = handle->prediction->predData;
    int32_t n = 0;
    for (; n < capacity && n < static_cast<int32_t>(data.size()); n++) out[n].physics = ToRsb(data[n]);
    return n;
}

/// Standalone ball rollout from an arbitrary state (does not disturb the live arena prediction).
RSB_EXPORT int32_t rsb_rollout_ball(void* arenaHandle, const RsbBallState* start, int32_t ticks,
                                    RsbBallState* out, int32_t capacity) {
    auto* handle = static_cast<ArenaHandle*>(arenaHandle);
    static thread_local std::map<Arena*, std::unique_ptr<BallPredTracker>> scratch;
    auto& tracker = scratch[handle->arena];
    if (!tracker || tracker->numPredTicks != static_cast<size_t>(ticks))
        tracker = std::make_unique<BallPredTracker>(handle->arena, static_cast<size_t>(ticks));
    BallState s = handle->arena->ball->GetState();
    FromRsb(start->physics, s);
    tracker->ForceUpdateAllPred(s);
    int32_t n = 0;
    for (; n < capacity && n < static_cast<int32_t>(tracker->predData.size()); n++)
        out[n].physics = ToRsb(tracker->predData[n]);
    return n;
}
