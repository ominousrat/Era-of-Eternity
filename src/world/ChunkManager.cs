using Godot;
using System;
using System.Collections.Generic;
using System.Threading;

[GlobalClass]
public partial class ChunkManager : Node
{
	[Export] public Godot.Collections.Array<Color> Colors = new Godot.Collections.Array<Color>();
	[Export] public Vector3 Dimensions = new Vector3(512, 256, 512);
	[Export] public int ChunkSize = 16;
	[Export] public int NoiseSeed = 0;
	[Export] public int RegionSizeXZ = 4;
	[Export] public int ThreadCount = 8;

	private FastNoiseLite _noise = new FastNoiseLite();
	private Vector3 _numberOfChunks;
	private int _totalChunks;

	// Threading
	private readonly List<Thread> _workerThreads = new();
	private readonly object _dataLock = new();
	private readonly List<ChunkData> _generatedChunks = new();
	private bool _generationStarted = false;
	private bool _generationFinished = false;

	private PackedScene _regionPref = GD.Load<PackedScene>("res://scenes/region.tscn");

	private double _startTimeUsec = 0;

	public override void _Ready()
	{
		// Noise
		_noise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
		_noise.Frequency = 0.0015f;
		_noise.Seed = NoiseSeed;

		_numberOfChunks = new Vector3(
			Dimensions.X / ChunkSize,
			Dimensions.Y / ChunkSize,
			Dimensions.Z / ChunkSize
		);
		_totalChunks = (int)(_numberOfChunks.X * _numberOfChunks.Y * _numberOfChunks.Z);

		_startTimeUsec = Time.GetTicksUsec();

		StartGenerationThreads();
	}

	public override void _Process(double delta)
	{
		if (!_generationStarted)
			return;

		if (!_generationFinished)
		{
			// Check if all threads finished
			bool allDone = true;
			foreach (var t in _workerThreads)
			{
				if (t.IsAlive)
				{
					allDone = false;
					break;
				}
			}
			if (!allDone)
				return;

			_generationFinished = true;
			GD.Print($"Threaded generation complete. Chunks: {_generatedChunks.Count}");
		}

		// Once finished, build regions exactly once
		if (_generationFinished)
		{
			BuildRegionsFromData();
			_generationFinished = false; // prevent re-running
			double endTime = Time.GetTicksUsec();
			double deltaTime = (endTime - _startTimeUsec) / 1_000_000.0;
			GD.Print($"World build time (threaded): {deltaTime:F2}s");
		}
	}

	// ---- Threaded generation of pure data ----

	private void StartGenerationThreads()
	{
		if (_generationStarted)
			return;

		_generationStarted = true;

		int maxThreads = Math.Max(1, Math.Min(ThreadCount, System.Environment.ProcessorCount - 1));

		_workerThreads.Clear();

		// Build list of chunk coords
		var coords = new List<Vector3I>();
		for (int x = 0; x < _numberOfChunks.X; x++)
		{
			for (int y = 0; y < _numberOfChunks.Y; y++)
			{
				for (int z = 0; z < _numberOfChunks.Z; z++)
				{
					coords.Add(new Vector3I(x, y, z));
				}
			}
		}

		// Split coords per thread
		var perThread = new List<List<Vector3I>>(maxThreads);
		for (int i = 0; i < maxThreads; i++)
			perThread.Add(new List<Vector3I>());

		for (int i = 0; i < coords.Count; i++)
		{
			perThread[i % maxThreads].Add(coords[i]);
		}

		for (int i = 0; i < maxThreads; i++)
		{
			var list = perThread[i];
			if (list.Count == 0)
				continue;

			var thread = new Thread(ThreadGenerateChunks);
			thread.IsBackground = true;
			_workerThreads.Add(thread);
			thread.Start(list);
		}

		GD.Print($"Started { _workerThreads.Count } generation threads.");
	}

	private void ThreadGenerateChunks(object? obj)
	{
		var list = (List<Vector3I>)obj!;
		int maxHeight = (int)Dimensions.Y;

		foreach (var coord in list)
		{
			Vector3 origin = new Vector3(
				coord.X * ChunkSize,
				coord.Y * ChunkSize,
				coord.Z * ChunkSize);

			// Generate blocks for this chunk (pure data)
			uint[] blocks = GenerateChunkBlocks(coord, origin, ChunkSize, maxHeight);

			if (blocks.Length == 0)
				continue;

			var data = new ChunkData
			{
				ChunkCoord = coord,
				Origin = origin,
				Blocks = blocks
			};

			lock (_dataLock)
			{
				_generatedChunks.Add(data);
			}
		}
	}

	private uint[] GenerateChunkBlocks(
		Vector3I chunkCoord,
		Vector3 origin,
		int chunkSize,
		int maxHeight)
	{
		List<uint> blocks = new List<uint>();
		float originY = origin.Y;

		for (int x = 0; x < chunkSize; x++)
		{
			for (int z = 0; z < chunkSize; z++)
			{
				Vector3 globalPos = origin + new Vector3(x, 0, z);

				float value = (float)(
					(_noise.GetNoise2D(globalPos.X, globalPos.Z)
					+ 0.5 * _noise.GetNoise2D(2 * globalPos.X, 2 * globalPos.Z)
					+ 0.25 * _noise.GetNoise2D(4 * globalPos.X, 4 * globalPos.Z) + 1) / 2.0);

				float valuePow = Mathf.Pow(value, 2.1f);
				float height = maxHeight * valuePow;

				if (height < originY * valuePow)
					continue;

				float localHeight = height - originY;
				int maxY = Mathf.Min((int)localHeight, chunkSize);

				for (int y = 0; y < maxY; y++)
				{
					Color c = Colors.Count > 0
						? Colors[y % Colors.Count]
						: new Color(0.4f, 0.8f, 0.4f);

					blocks.Add(ChunkPackBlock(x, y, z, c));
				}
			}
		}

		return blocks.ToArray();
	}

	private static uint ChunkPackBlock(int x, int y, int z, Color color)
	{
		x = Mathf.Clamp(x, 0, 15);
		y = Mathf.Clamp(y, 0, 15);
		z = Mathf.Clamp(z, 0, 15);

		int r = Mathf.Clamp((int)(color.R * 7.0f + 0.5f), 0, 7);
		int g = Mathf.Clamp((int)(color.G * 7.0f + 0.5f), 0, 7);
		int b = Mathf.Clamp((int)(color.B * 7.0f + 0.5f), 0, 7);
		int a = Mathf.Clamp((int)(color.A * 7.0f + 0.5f), 0, 7);

		uint p = 0;
		p |= (uint)(x & 0xF);
		p |= (uint)((y & 0xF) << 4);
		p |= (uint)((z & 0xF) << 8);
		p |= (uint)((r & 0x7) << 12);
		p |= (uint)((g & 0x7) << 15);
		p |= (uint)((b & 0x7) << 18);
		p |= (uint)((a & 0x7) << 21);
		return p;
	}

	// ---- Build Regions from data on main thread ----

	private Vector3I GetRegionCoord(Vector3I chunkCoord)
	{
		return new Vector3I(
			chunkCoord.X / RegionSizeXZ,
			0,
			chunkCoord.Z / RegionSizeXZ
		);
	}

	private void BuildRegionsFromData()
	{
		// Group per region
		var regionToData = new Dictionary<Vector3I, List<ChunkData>>();
		lock (_dataLock)
		{
			foreach (var data in _generatedChunks)
			{
				var regionCoord = GetRegionCoord(data.ChunkCoord);
				if (!regionToData.TryGetValue(regionCoord, out var list))
				{
					list = new List<ChunkData>();
					regionToData[regionCoord] = list;
				}
				list.Add(data);
			}
		}

		// For each region, build a Region node and mesh
		foreach (var kv in regionToData)
		{
			var regionNode = (Region)_regionPref.Instantiate();
			regionNode.Name = $"Region_{kv.Key.X}_{kv.Key.Z}";
			AddChild(regionNode);
			regionNode.Position = Vector3.Zero;

			// Convert ChunkData into Chunk instances (in memory only)
			var chunks = new List<Chunk>();
			foreach (var data in kv.Value)
			{
				var chunk = new Chunk();
				chunk.Position = data.Origin;

				// Fill chunk’s block HashSet and mesh arrays on main thread
				ChunkFillFromData(chunk, data.Blocks);
				chunk.GenerateMeshData(); // uses the blocks we just filled

				chunks.Add(chunk);
			}

			regionNode.BuildFromChunks(chunks);
		}
	}

	private void ChunkFillFromData(Chunk chunk, uint[] blocks)
	{
		// Access private _blocks via reflection is messy; easier:
		// add a public method to Chunk: SetBlocks(uint[])
		chunk.SetBlocks(blocks);
	}

	public override void _ExitTree()
	{
		foreach (var t in _workerThreads)
		{
			if (t.IsAlive)
				t.Join();
		}
	}
}
