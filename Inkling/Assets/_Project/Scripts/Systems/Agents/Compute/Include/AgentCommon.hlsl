// AgentCommon.hlsl - Shared agent definitions for GPU compute
// Must match Agent.cs struct layout exactly

#ifndef AGENT_COMMON_INCLUDED
#define AGENT_COMMON_INCLUDED

// Agent struct - 36 bytes (9 floats)
// Layout must match C# Agent struct in Agent.cs
struct Agent
{
    float2 position;      // UV space [0,1]
    float2 velocity;      // Current velocity (UV/s)
    float2 flockForce;    // Computed flocking steering
    float advectionWeight;  // 0=ignore fluid, 1=full advection
    float flockWeight;      // 0=solo, 1=full flocking
    uint flags;             // Bit 0: active, Bits 1-4: inkType, Bits 5-7: behaviorId
};

// Flag accessors
bool IsActive(Agent a) { return (a.flags & 1) != 0; }
int GetInkType(Agent a) { return (a.flags >> 1) & 0xF; }
int GetBehaviorId(Agent a) { return (a.flags >> 5) & 0x7; }

// Flag setters
void SetActive(inout Agent a, bool active)
{
    if (active)
        a.flags |= 1;
    else
        a.flags &= ~1u;
}

void SetInkType(inout Agent a, int inkType)
{
    a.flags = (a.flags & ~(0xFu << 1)) | ((uint)(inkType & 0xF) << 1);
}

void SetBehaviorId(inout Agent a, int behaviorId)
{
    a.flags = (a.flags & ~(0x7u << 5)) | ((uint)(behaviorId & 0x7) << 5);
}

// Create inactive agent
Agent CreateInactiveAgent()
{
    Agent a;
    a.position = float2(0, 0);
    a.velocity = float2(0, 0);
    a.flockForce = float2(0, 0);
    a.advectionWeight = 0;
    a.flockWeight = 0;
    a.flags = 0;
    return a;
}

// Flocking parameters (set from C#)
cbuffer AgentParams : register(b0)
{
    uint _AgentCount;           // Number of agents in buffer
    float _DeltaTime;           // Time step in seconds
    float _NeighborRadius;      // Flocking neighbor search radius (UV space)
    float _AlignmentWeight;     // Alignment steering weight
    float _CohesionWeight;      // Cohesion steering weight
    float _SeparationWeight;    // Separation steering weight
    float _MaxSpeed;            // Maximum agent speed (UV/s)
    float _MaxForce;            // Maximum steering force
    float2 _SimulationSize;     // Velocity texture resolution
};

// Thread group size for agent kernels
#define AGENT_THREAD_GROUP_SIZE 64

// Safe normalize that returns zero for zero-length vectors
float2 SafeNormalize(float2 v)
{
    float len = length(v);
    return len > 0.0001 ? v / len : float2(0, 0);
}

// Clamp vector to maximum magnitude
float2 ClampMagnitude(float2 v, float maxMag)
{
    float len = length(v);
    return len > maxMag ? v * (maxMag / len) : v;
}

#endif // AGENT_COMMON_INCLUDED
