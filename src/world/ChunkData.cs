using Godot;

public struct ChunkData
{
	public Vector3I ChunkCoord; // Chunk index in chunk grid
	public Vector3 Origin;      // World-space origin of this chunk
	public uint[] Blocks;       // Packed blocks for this chunk
}
