using Godot;
using System.Collections.Generic;

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

	public void BuildFromChunks(IEnumerable<Chunk> chunks)
	{
		var allVertices = new List<Vector3>();
		var allNormals  = new List<Vector3>();
		var allColors   = new List<Color>();

		foreach (var chunk in chunks)
		{
			var (v, n, c) = chunk.GetMeshData();
			if (v == null || v.Length == 0)
				continue;

			// Chunk.Position is the world origin of that chunk.
			for (int i = 0; i < v.Length; i++)
			{
				allVertices.Add(v[i] + chunk.Position);
				allNormals.Add(n[i]);
				allColors.Add(c[i]);
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
