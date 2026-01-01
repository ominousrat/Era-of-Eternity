using Godot;
using System;
using System.Collections.Generic;

public partial class Chunk : StaticBody3D
{
	[Export] public StandardMaterial3D Material;
	[Export] public CollisionShape3D CollisionShape;
	[Export] public MeshInstance3D MeshInstance;

	// Packed block:  0–3: X, 4–7: Y, 8–11: Z, 12–14: R, 15–17: G, 18–20: B, 21–23: A.
	private readonly HashSet<uint> _blocks = new();

	private Godot.Collections.Array _surfaceArray = new Godot.Collections.Array();
	private int _minHeight = 0;

	private Vector3[] _verticesArray = Array.Empty<Vector3>();
	private Vector3[] _normalsArray  = Array.Empty<Vector3>();
	private Color[]   _colorsArray   = Array.Empty<Color>();

	private static readonly Vector3[] BlockVertices =
	{
		new Vector3(-0.5f, -0.5f,  0.5f), // 0
		new Vector3(-0.5f,  0.5f,  0.5f), // 1
		new Vector3( 0.5f,  0.5f,  0.5f), // 2
		new Vector3( 0.5f, -0.5f,  0.5f), // 3

		new Vector3(-0.5f, -0.5f, -0.5f), // 4
		new Vector3(-0.5f,  0.5f, -0.5f), // 5
		new Vector3( 0.5f,  0.5f, -0.5f), // 6
		new Vector3( 0.5f, -0.5f, -0.5f), // 7
	};

	private enum Face { Right, Left, Top, Bottom, Front, Back }

	private static readonly Dictionary<Face, int[][]> FaceIndices = new()
	{
		{ Face.Right,  new[] { new[] {3,6,7}, new[] {3,2,6} } },
		{ Face.Left,   new[] { new[] {4,1,0}, new[] {4,5,1} } },

		{ Face.Top,    new[] { new[] {1,6,2}, new[] {1,5,6} } },
		{ Face.Bottom, new[] { new[] {4,3,7}, new[] {4,0,3} } },

		{ Face.Front,  new[] { new[] {0,2,3}, new[] {0,1,2} } },
		{ Face.Back,   new[] { new[] {7,5,4}, new[] {7,6,5} } },
	};

	private static readonly Dictionary<Face, Vector3> FaceNormals = new()
	{
		{ Face.Right,  new Vector3( 1,  0,  0) },
		{ Face.Left,   new Vector3(-1,  0,  0) },

		{ Face.Top,    new Vector3( 0,  1,  0) },
		{ Face.Bottom, new Vector3( 0, -1,  0) },

		{ Face.Front,  new Vector3( 0,  0,  1) },
		{ Face.Back,   new Vector3( 0,  0, -1) },
	};

	public override void _Ready()
	{
		_surfaceArray = new Godot.Collections.Array();
		_surfaceArray.Resize((int)Mesh.ArrayType.Max);
		MeshInstance.Mesh = new ArrayMesh();
	}

	// ----- Packing helpers -----

	private static uint PackBlock(int x, int y, int z, Color color)
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

	private static void UnpackBlock(uint packed, out int x, out int y, out int z, out Color color)
	{
		x = (int)(packed & 0xF);
		y = (int)((packed >> 4) & 0xF);
		z = (int)((packed >> 8) & 0xF);

		int r = (int)((packed >> 12) & 0x7);
		int g = (int)((packed >> 15) & 0x7);
		int b = (int)((packed >> 18) & 0x7);
		int a = (int)((packed >> 21) & 0x7);

		color = new Color(r / 7.0f, g / 7.0f, b / 7.0f, a / 7.0f);
	}

	// ----- Data generation -----

	public int GenerateData(
		int chunkSize,
		int maxHeight,
		FastNoiseLite noise,
		Godot.Collections.Array<Color> colorArray,
		Vector3 chunkOrigin)
	{
		_minHeight = maxHeight + 1;
		_blocks.Clear();

		float originY = chunkOrigin.Y;

		for (int x = 0; x < chunkSize; x++)
		{
			for (int z = 0; z < chunkSize; z++)
			{
				Vector3 globalPos = chunkOrigin + new Vector3(x, 0, z);

				float value = (float)(
					(noise.GetNoise2D(globalPos.X, globalPos.Z)
					+ 0.5 * noise.GetNoise2D(2 * globalPos.X, 2 * globalPos.Z)
					+ 0.25 * noise.GetNoise2D(4 * globalPos.X, 4 * globalPos.Z) + 1) / 2.0);

				float valuePow = Mathf.Pow(value, 2.1f);
				float height = maxHeight * valuePow;

				if (height < originY * valuePow)
					continue;

				float localHeight = height - originY;

				if (height < _minHeight)
					_minHeight = (int)height;

				int maxY = Mathf.Min((int)localHeight, chunkSize);
				for (int y = 0; y < maxY; y++)
				{
					Color c = colorArray[y % colorArray.Count];
					_blocks.Add(PackBlock(x, y, z, c));
				}
			}
		}

		return _minHeight;
	}

	// ----- Mesh generation (CPU only, no neighbours) -----

	public void GenerateMeshData()
	{
		var vertices = new List<Vector3>();
		var normals  = new List<Vector3>();
		var colors   = new List<Color>();

		foreach (uint packed in _blocks)
		{
			UnpackBlock(packed, out int x, out int y, out int z, out Color color);

			// Local block position inside chunk (0..chunkSize-1)
			Vector3 pos = new Vector3(x, y, z);

			// Add all 6 faces, no culling (fixes cross‑chunk artefacts)
			AddFace(Face.Right,  pos, color, vertices, normals, colors);
			AddFace(Face.Left,   pos, color, vertices, normals, colors);
			AddFace(Face.Top,    pos, color, vertices, normals, colors);
			AddFace(Face.Bottom, pos, color, vertices, normals, colors);
			AddFace(Face.Front,  pos, color, vertices, normals, colors);
			AddFace(Face.Back,   pos, color, vertices, normals, colors);
		}

		_verticesArray = vertices.ToArray();
		_normalsArray  = normals.ToArray();
		_colorsArray   = colors.ToArray();
	}

	private void AddFace(
		Face face,
		Vector3 pos,
		Color color,
		List<Vector3> vertices,
		List<Vector3> normals,
		List<Color> colors)
	{
		int[][] indices = FaceIndices[face];
		Vector3 normal = FaceNormals[face];

		foreach (int[] tri in indices)
		{
			foreach (int idx in tri)
			{
				vertices.Add(BlockVertices[idx] + pos);
				normals.Add(normal);
				colors.Add(color);
			}
		}
	}

	// ----- Region integration -----

	public (Vector3[] vertices, Vector3[] normals, Color[] colors) GetMeshData()
	{
		return (_verticesArray, _normalsArray, _colorsArray);
	}
	
	public void SetBlocks(uint[] blocks)
{
	_blocks.Clear();
	if (blocks == null)
		return;

	foreach (var b in blocks)
		_blocks.Add(b);
}

}
