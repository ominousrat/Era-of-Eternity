using Godot;
using System.Collections.Generic;

/// <summary>
/// Lightweight chunk mesh data without Node overhead
/// </summary>
public struct ChunkMeshData
{
	public Vector3 Position;
	public Vector3[] Vertices;
	public Vector3[] Normals;
	public Color[] Colors;
}

public partial class Region : StaticBody3D
{
	[Export] public StandardMaterial3D Material;
	[Export] public MeshInstance3D MeshInstance;
	[Export] public CollisionShape3D CollisionShape;

	private ArrayMesh _mesh;
	private Godot.Collections.Array _surfaceArray;

	public override void _Ready()
	{
		if (MeshInstance == null)
		{
			MeshInstance = new MeshInstance3D();
			AddChild(MeshInstance);
		}

		if (CollisionShape == null)
		{
			CollisionShape = new CollisionShape3D();
			AddChild(CollisionShape);
		}

		_mesh = new ArrayMesh();
		MeshInstance.Mesh = _mesh;

		_surfaceArray = new Godot.Collections.Array();
		_surfaceArray.Resize((int)Mesh.ArrayType.Max);
	}

	/// <summary>
	/// Build mesh from Chunk objects (legacy method - creates temporary nodes)
	/// </summary>
	public void BuildFromChunks(IEnumerable<Chunk> chunks)
	{
		var meshDataList = new List<ChunkMeshData>();
		
		foreach (var chunk in chunks)
		{
			var (v, n, c) = chunk.GetMeshData();
			if (v == null || v.Length == 0)
				continue;
				
			meshDataList.Add(new ChunkMeshData
			{
				Position = chunk.Position,
				Vertices = v,
				Normals = n,
				Colors = c
			});
		}
		
		BuildFromMeshData(meshDataList);
	}
	
	/// <summary>
	/// Build mesh from lightweight ChunkMeshData (no Node overhead)
	/// </summary>
	public void BuildFromMeshData(IEnumerable<ChunkMeshData> meshDataList)
	{
		// Ensure _Ready() has been called (initialize if needed)
		if (_surfaceArray == null)
		{
			if (MeshInstance == null)
			{
				MeshInstance = new MeshInstance3D();
				AddChild(MeshInstance);
			}

			if (CollisionShape == null)
			{
				CollisionShape = new CollisionShape3D();
				AddChild(CollisionShape);
			}

			_mesh = new ArrayMesh();
			MeshInstance.Mesh = _mesh;

			_surfaceArray = new Godot.Collections.Array();
			_surfaceArray.Resize((int)Mesh.ArrayType.Max);
		}

		var allVertices = new List<Vector3>();
		var allNormals  = new List<Vector3>();
		var allColors   = new List<Color>();

		foreach (var meshData in meshDataList)
		{
			if (meshData.Vertices == null || meshData.Vertices.Length == 0)
				continue;

			// Add vertices offset by chunk position
			for (int i = 0; i < meshData.Vertices.Length; i++)
			{
				allVertices.Add(meshData.Vertices[i] + meshData.Position);
				allNormals.Add(meshData.Normals[i]);
				allColors.Add(meshData.Colors[i]);
			}
		}

		if (allVertices.Count == 0)
			return;

		_surfaceArray[(int)Mesh.ArrayType.Vertex] = allVertices.ToArray();
		_surfaceArray[(int)Mesh.ArrayType.Normal] = allNormals.ToArray();
		_surfaceArray[(int)Mesh.ArrayType.Color]  = allColors.ToArray();

		_mesh.ClearSurfaces();
		_mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, _surfaceArray); // one surface = one drawcall [web:123]

		if (Material != null)
			_mesh.SurfaceSetMaterial(0, Material);

		if (CollisionShape != null)
		{
			var shape = _mesh.CreateTrimeshShape(); // static terrain collision [web:27]
			CollisionShape.Shape = shape;
		}
	}
}
