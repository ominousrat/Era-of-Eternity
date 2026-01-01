using Godot;

/// Plain data used by worker threads (no Nodes, no Godot objects except Vector3I/Vector3).
public struct ChunkData
{
	public Vector3I ChunkCoord; // Chunk index in chunk grid
	public Vector3 Origin;      // World-space origin of this chunk
	public uint[] Blocks;       // Packed blocks for this chunk
}
