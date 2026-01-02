using Godot;
using System;
using System.Collections.Generic;
using System.Threading;

/// <summary>
/// Manages the threaded generation of Voxel Chunks and groups them into Regions.
/// Designed for Godot 4.6.
/// </summary>
[GlobalClass]
public partial class ChunkManager : Node
{
	[ExportCategory("Voxel Configuration")]
	[Export] public Vector3 WorldDimensions = new Vector3(1024, 256, 1024); // Reduced from 512x256x512 to prevent OOM
	[Export] public int ChunkSize = 16;
	[Export] public int RegionSizeXZ = 16;
	[Export] public Godot.Collections.Array<Color> LayerColors = new();

	[ExportCategory("Generation Settings")]
	[Export] public int NoiseSeed = 0;
	[Export] public int ThreadCount = 8;

	private FastNoiseLite _noise = new();
	private Vector3I _totalChunksGrid;
	private PackedScene _regionScene;

	// Threading Synchronization
	private readonly List<Thread> _activeThreads = new();
	private readonly object _bufferLock = new();
	private readonly List<ChunkData> _threadSafeChunkBuffer = new(); // Output from threads
	
	// State Tracking
	private bool _isGenerating = false;
	private bool _hasFinishedGeneration = false;
	private double _startTimeUsec = 0;
	
	// Memory management for large worlds
	private int _chunksProcessed = 0;
	private const int MAX_CHUNKS_PER_BATCH = 256; // Limit chunks per batch to prevent memory issues

	public override void _Ready()
	{
		// Load scene in _Ready() instead of field initialization
		_regionScene = GD.Load<PackedScene>("res://scenes/region.tscn");
		if (_regionScene == null)
		{
			GD.PrintErr("[ChunkManager] Failed to load region.tscn scene!");
			return;
		}

		InitializeNoise();
		CalculateWorldGrid();

		_startTimeUsec = Time.GetTicksUsec();
		StartGenerationThreads();
	}

	public override void _Process(double delta)
	{
		if (!_isGenerating || _hasFinishedGeneration)
			return;

		CheckThreadStatus();
	}

	public override void _ExitTree()
	{
		// Ensure all threads close cleanly when the game stops
		foreach (var thread in _activeThreads)
		{
			if (thread.IsAlive) thread.Join();
		}
		
		// Clear any remaining data to prevent memory leaks
		lock (_bufferLock)
		{
			_threadSafeChunkBuffer.Clear();
		}
	}

	// --------------------------------------------------------------------------
	// Initialization & Setup
	// --------------------------------------------------------------------------

	private void InitializeNoise()
	{
		_noise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
		_noise.Frequency = 0.0015f;
		_noise.Seed = NoiseSeed;
	}

	private void CalculateWorldGrid()
	{
		_totalChunksGrid = new Vector3I(
			(int)(WorldDimensions.X / ChunkSize),
			(int)(WorldDimensions.Y / ChunkSize),
			(int)(WorldDimensions.Z / ChunkSize)
		);
		
		int totalChunks = _totalChunksGrid.X * _totalChunksGrid.Y * _totalChunksGrid.Z;
		GD.Print($"[ChunkManager] World Grid: {_totalChunksGrid} = {totalChunks} total chunks");
		
		// Safety check - warn if generating too many chunks
		if (totalChunks > 5000)
		{
			GD.PrintErr($"[ChunkManager] WARNING: Generating {totalChunks} chunks may cause memory issues!");
		}
	}

	// --------------------------------------------------------------------------
	// Thread Management
	// --------------------------------------------------------------------------

	private void StartGenerationThreads()
	{
		if (_isGenerating) return;
		_isGenerating = true;

		// New approach: Process world in batches to prevent memory overflow with large worlds
		var chunkBatches = GenerateChunkBatches();
		
		// Create a single thread pool for processing chunks in batches
		int logicalCores = Math.Max(1, System.Environment.ProcessorCount - 1);
		int maxWorkers = Math.Min(ThreadCount, logicalCores);
		
		_activeThreads.Clear();

		for (int i = 0; i < maxWorkers; i++)
		{
			Thread thread = new Thread(Worker_GenerateBatches);
			thread.IsBackground = true;
			thread.Start(i); // Pass thread index for identification
			_activeThreads.Add(thread);
		}

		GD.Print($"[ChunkManager] Started {_activeThreads.Count} threads for generation.");
	}
	
	private List<List<Vector3I>> GenerateChunkBatches()
	{
		var allCoordinates = new List<Vector3I>();
		
		// Create list of coordinates in chunks
		for (int x = 0; x < _totalChunksGrid.X; x++)
		{
			for (int y = 0; y < _totalChunksGrid.Y; y++)
			{
				for (int z = 0; z < _totalChunksGrid.Z; z++)
				{
					allCoordinates.Add(new Vector3I(x, y, z));
				}
			}
		}

		var batches = new List<List<Vector3I>>();
		
		// Process in smaller batches to prevent memory issues
		for (int i = 0; i < allCoordinates.Count; i += MAX_CHUNKS_PER_BATCH)
		{
			int count = Math.Min(MAX_CHUNKS_PER_BATCH, allCoordinates.Count - i);
			var batch = new List<Vector3I>();
			
			for (int j = 0; j < count; j++)
			{
				batch.Add(allCoordinates[i + j]);
			}
			
			batches.Add(batch);
		}
		
		GD.Print($"[ChunkManager] Generated {batches.Count} batches for processing");
		return batches;
	}

	private void CheckThreadStatus()
	{
		foreach (var t in _activeThreads)
		{
			if (t.IsAlive) return; // Still working
		}

		// If we reach here, all threads are dead
		FinishGeneration();
	}

	private void FinishGeneration()
	{
		_hasFinishedGeneration = true;
		_isGenerating = false;

		GD.Print($"[ChunkManager] Generation Threads Finished. Buffer Size: {_threadSafeChunkBuffer.Count}");

		BuildRegionsFromBuffer();

		double totalSeconds = (Time.GetTicksUsec() - _startTimeUsec) / 1_000_000.0;
		GD.Print($"[ChunkManager] World Build Complete in {totalSeconds:F2}s");
	}

	// --------------------------------------------------------------------------
	// Worker Logic (Runs on Background Threads)
	// --------------------------------------------------------------------------

	private void Worker_GenerateBatches(object? state)
	{
		int threadIndex = (int)state!;
		
		// Process chunks in batches to avoid memory issues
		var allCoordinates = new List<Vector3I>();
		
		for (int x = 0; x < _totalChunksGrid.X; x++)
		{
			for (int y = 0; y < _totalChunksGrid.Y; y++)
			{
				for (int z = 0; z < _totalChunksGrid.Z; z++)
				{
					allCoordinates.Add(new Vector3I(x, y, z));
				}
			}
		}

		int worldHeightLimit = (int)WorldDimensions.Y;

		// Create thread-local FastNoiseLite instance (not thread-safe to share)
		FastNoiseLite threadNoise = new FastNoiseLite();
		threadNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
		threadNoise.Frequency = _noise.Frequency;
		threadNoise.Seed = _noise.Seed;

		int processedCount = 0;
		
		for (int i = 0; i < allCoordinates.Count; i++)
		{
			var coord = allCoordinates[i];
			
			Vector3 origin = new Vector3(coord.X * ChunkSize, coord.Y * ChunkSize, coord.Z * ChunkSize);
			
			// Expensive math here
			uint[] blockData = CalculateBlockData(origin, worldHeightLimit, threadNoise);

			if (blockData.Length == 0) continue; // Skip empty chunks to save memory

			var dataPacket = new ChunkData
			{
				ChunkCoord = coord,
				Origin = origin,
				Blocks = blockData
			};

			// Thread-safe write to main buffer
			lock (_bufferLock)
			{
				_threadSafeChunkBuffer.Add(dataPacket);
				_chunksProcessed++;
				
				// Periodic logging for large worlds
				if (_chunksProcessed % 100 == 0)
				{
					GD.Print($"[ChunkManager] Thread {threadIndex}: Processed {_chunksProcessed} chunks");
				}
			}
			
			processedCount++;
		}
		
		GD.Print($"[ChunkManager] Thread {threadIndex}: Finished processing {processedCount} chunks");
	}

	private uint[] CalculateBlockData(Vector3 origin, int maxHeight, FastNoiseLite noise)
	{
		List<uint> localBlocks = new List<uint>();
		float originY = origin.Y;

		for (int x = 0; x < ChunkSize; x++)
		{
			for (int z = 0; z < ChunkSize; z++)
			{
				Vector3 globalPos = origin + new Vector3(x, 0, z);
				
				// Combine octaves manually for specific control, or use Noise properties
				float n1 = noise.GetNoise2D(globalPos.X, globalPos.Z);
				float n2 = noise.GetNoise2D(globalPos.X * 2, globalPos.Z * 2) * 0.5f;
				float n3 = noise.GetNoise2D(globalPos.X * 4, globalPos.Z * 4) * 0.25f;
				
				// Normalize 0..1 roughly
				float noiseValue = (n1 + n2 + n3 + 1) / 2.0f; 
				float heightMapY = maxHeight * Mathf.Pow(noiseValue, 2.1f);

				// If the terrain height is lower than the bottom of this chunk, it's empty air
				if (heightMapY < originY) continue;

				// Calculate how high we fill within this specific chunk (0 to ChunkSize)
				float relativeHeight = heightMapY - originY;
				int fillHeight = Mathf.Min((int)relativeHeight, ChunkSize);

				for (int y = 0; y < fillHeight; y++)
				{
					Color colorToUse = LayerColors.Count > 0 
						? LayerColors[y % LayerColors.Count] 
						: new Color(0.4f, 0.8f, 0.4f);

					localBlocks.Add(BlockPacker.Pack(x, y, z, colorToUse));
				}
			}
		}

		return localBlocks.ToArray();
	}

	// --------------------------------------------------------------------------
	// Main Thread Mesh Building
	// --------------------------------------------------------------------------

	private void BuildRegionsFromBuffer()
	{
		if (_regionScene == null)
		{
			GD.PrintErr("[ChunkManager] Cannot build regions: region scene is null!");
			return;
		}

		// 1. Sort chunks into buckets based on their Region
		var regionBuckets = new Dictionary<Vector3I, List<ChunkData>>();

		lock (_bufferLock)
		{
			foreach (var chunkData in _threadSafeChunkBuffer)
			{
				Vector3I regionCoord = new Vector3I(
					chunkData.ChunkCoord.X / RegionSizeXZ,
					0, // Regions usually handle full height columns in simple implementations
					chunkData.ChunkCoord.Z / RegionSizeXZ
				);

				if (!regionBuckets.ContainsKey(regionCoord))
				{
					regionBuckets[regionCoord] = new List<ChunkData>();
				}
				regionBuckets[regionCoord].Add(chunkData);
			}
		}

		// 2. Instantiate Regions
		int regionCount = 0;
		foreach (var entry in regionBuckets)
		{
			Vector3I gridPos = entry.Key;
			List<ChunkData> chunksInData = entry.Value;

			Region regionNode = _regionScene.Instantiate<Region>();
			regionNode.Name = $"Region_{gridPos.X}_{gridPos.Z}";
			AddChild(regionNode);
			
			// Build a HashSet of all world-space block positions from all chunks in this region
			// This allows cross-chunk face culling
			var worldBlockPositions = new HashSet<Vector3I>();
			var chunkLocalPositions = new Dictionary<Vector3, (uint[] blocks, HashSet<Vector3I> localPositions)>();
			
			foreach (var data in chunksInData)
			{
				// Build local position set for this chunk
				var localPositions = new HashSet<Vector3I>();
				foreach (uint packed in data.Blocks)
				{
					Chunk.UnpackBlock(packed, out int x, out int y, out int z, out Color _);
					localPositions.Add(new Vector3I(x, y, z));
					
					// Convert to world position and add to global set
					Vector3 worldPos = data.Origin + new Vector3(x, y, z);
					Vector3I worldPosInt = new Vector3I(
						(int)Mathf.Floor(worldPos.X),
						(int)Mathf.Floor(worldPos.Y),
						(int)Mathf.Floor(worldPos.Z)
					);
					worldBlockPositions.Add(worldPosInt);
				}
				chunkLocalPositions[data.Origin] = (data.Blocks, localPositions);
			}
			
			// Generate mesh data with cross-chunk culling
			List<ChunkMeshData> meshDataList = new List<ChunkMeshData>();
			foreach (var data in chunksInData)
			{
				var (blocks, localPositions) = chunkLocalPositions[data.Origin];
				var (vertices, normals, colors) = Chunk.GenerateMeshDataWithCulling(
					blocks, 
					localPositions, 
					worldBlockPositions, 
					data.Origin
				);
				
				meshDataList.Add(new ChunkMeshData
				{
					Position = data.Origin,
					Vertices = vertices,
					Normals = normals,
					Colors = colors
				});
			}

			regionNode.BuildFromMeshData(meshDataList);
			regionCount++;
		}
		
		GD.Print($"[ChunkManager] Built {regionCount} regions. Clearing chunk buffer...");
		
		// Clear the chunk buffer to free memory after processing
		lock (_bufferLock)
		{
			_threadSafeChunkBuffer.Clear();
		}
	}
}

/// <summary>
/// Bit-packing utility to compress Block data (Position + Color) into a single uint.
/// Format: X(4) Y(4) Z(4) R(3) G(3) B(3) A(3)
/// </summary>
public static class BlockPacker
{
	public static uint Pack(int x, int y, int z, Color color)
	{
		// Clamp coordinates 0-15
		uint ux = (uint)(x & 0xF);
		uint uy = (uint)(y & 0xF);
		uint uz = (uint)(z & 0xF);

		// Quantize color to 3 bits (0-7 range)
		uint r = (uint)Mathf.Clamp((int)(color.R * 7.5f), 0, 7);
		uint g = (uint)Mathf.Clamp((int)(color.G * 7.5f), 0, 7);
		uint b = (uint)Mathf.Clamp((int)(color.B * 7.5f), 0, 7);
		uint a = (uint)Mathf.Clamp((int)(color.A * 7.5f), 0, 7);

		// Shift and Combine
		return ux |
			   (uy << 4) |
			   (uz << 8) |
			   (r << 12) |
			   (g << 15) |
			   (b << 18) |
			   (a << 21);
	}
}
