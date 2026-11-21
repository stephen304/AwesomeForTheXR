// Unity 4 renders to D3D11 eye textures with the opposite vertical origin to OpenXR.
float4 Fullscreen(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

Texture2D<float4> sourceEye : register(t0);
float4 FlipEye(float4 position : SV_Position) : SV_Target
{
    uint width, height;
    sourceEye.GetDimensions(width, height);
    return sourceEye.Load(int3(uint(position.x), height - 1 - uint(position.y), 0));
}
