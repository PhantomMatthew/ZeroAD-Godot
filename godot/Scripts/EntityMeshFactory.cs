using Godot;

namespace ZeroAD.Godot;

public static class EntityMeshFactory
{
    public static MeshInstance3D CreateVillager(Color teamColor)
    {
        var root = new MeshInstance3D();
        var body = new CylinderMesh();
        body.TopRadius = 0.4f; body.BottomRadius = 0.5f; body.Height = 1.6f;
        root.Mesh = body;
        root.MaterialOverride = CreateMat(teamColor);
        root.Position = new Vector3(0, 0.8f, 0);
        return root;
    }

    public static MeshInstance3D CreateSoldier(Color teamColor)
    {
        var root = new MeshInstance3D();
        var body = new CapsuleMesh();
        body.Radius = 0.35f; body.Height = 2.0f;
        root.Mesh = body;
        root.MaterialOverride = CreateMat(teamColor);
        root.Position = new Vector3(0, 1.0f, 0);

        var weapon = new MeshInstance3D();
        var sword = new BoxMesh();
        sword.Size = new Vector3(0.08f, 1.5f, 0.02f);
        weapon.Mesh = sword;
        weapon.MaterialOverride = CreateMat(new Color(0.8f, 0.8f, 0.9f));
        weapon.Position = new Vector3(0.5f, 1.0f, 0);
        root.AddChild(weapon);

        var shield = new MeshInstance3D();
        var disc = new CylinderMesh();
        disc.TopRadius = 0.4f; disc.BottomRadius = 0.4f; disc.Height = 0.06f;
        disc.Material = CreateMat(new Color(0.6f, 0.2f, 0.1f));
        shield.Mesh = disc;
        shield.Position = new Vector3(-0.45f, 1.0f, 0);
        shield.Rotation = new Vector3(0, 0, Mathf.Pi / 2);
        root.AddChild(shield);

        return root;
    }

    public static Node3D CreateTree()
    {
        var root = new Node3D();

        var trunk = new MeshInstance3D();
        var trunkMesh = new CylinderMesh();
        trunkMesh.TopRadius = 0.2f; trunkMesh.BottomRadius = 0.35f; trunkMesh.Height = 2.0f;
        trunk.Mesh = trunkMesh;
        trunk.MaterialOverride = CreateMat(new Color(0.35f, 0.22f, 0.12f));
        trunk.Position = new Vector3(0, 1.0f, 0);
        root.AddChild(trunk);

        for (int i = 0; i < 3; i++)
        {
            var foliage = new MeshInstance3D();
            var cone = new PrismMesh();
            cone.Size = new Vector3(2.5f - i * 0.5f, 2.0f, 2.5f - i * 0.5f);
            foliage.Mesh = cone;
            foliage.MaterialOverride = CreateMat(new Color(
                0.08f + i * 0.04f, 0.4f + i * 0.05f, 0.06f));
            foliage.Position = new Vector3(0, 2.5f + i * 1.2f, 0);
            root.AddChild(foliage);
        }

        return root;
    }

    public static Node3D CreateBuilding(Color teamColor, string name)
    {
        var root = new Node3D();
        float s = 6f;

        var walls = new MeshInstance3D();
        var box = new BoxMesh();
        box.Size = new Vector3(s, s * 0.7f, s);
        walls.Mesh = box;
        walls.MaterialOverride = CreateMat(teamColor);
        walls.Position = new Vector3(0, s * 0.35f, 0);
        root.AddChild(walls);

        var roof = new MeshInstance3D();
        var prism = new PrismMesh();
        prism.Size = new Vector3(s + 1, s * 0.5f, s + 1);
        roof.Mesh = prism;
        roof.MaterialOverride = CreateMat(new Color(0.4f, 0.15f, 0.08f));
        roof.Position = new Vector3(0, s * 0.7f + s * 0.25f, 0);
        root.AddChild(roof);

        if (name.Contains("Center") || name.Contains("civil_centre"))
        {
            root.AddChild(CreatePillar(new Vector3(-s / 2 - 0.3f, 0, -s / 2 - 0.3f), s));
            root.AddChild(CreatePillar(new Vector3(s / 2 + 0.3f, 0, -s / 2 - 0.3f), s));
            root.AddChild(CreatePillar(new Vector3(-s / 2 - 0.3f, 0, s / 2 + 0.3f), s));
            root.AddChild(CreatePillar(new Vector3(s / 2 + 0.3f, 0, s / 2 + 0.3f), s));
        }

        return root;
    }

    public static Node3D CreateFoundation(Color teamColor, float buildFraction)
    {
        var root = new Node3D();
        var box = new MeshInstance3D();
        var mesh = new BoxMesh();
        mesh.Size = new Vector3(5, 5 * buildFraction, 5);
        box.Mesh = mesh;
        box.MaterialOverride = CreateMat(new Color(
            teamColor.R * 0.5f, teamColor.G * 0.5f, teamColor.B * 0.5f, 0.5f));
        box.Position = new Vector3(0, 5 * buildFraction / 2, 0);
        root.AddChild(box);
        return root;
    }

    private static MeshInstance3D CreatePillar(Vector3 pos, float height)
    {
        var pillar = new MeshInstance3D();
        var cyl = new CylinderMesh();
        cyl.TopRadius = 0.3f; cyl.BottomRadius = 0.3f; cyl.Height = height * 0.7f;
        pillar.Mesh = cyl;
        pillar.MaterialOverride = CreateMat(new Color(0.9f, 0.88f, 0.82f));
        pillar.Position = new Vector3(pos.X, height * 0.35f, pos.Z);
        return pillar;
    }

    private static StandardMaterial3D CreateMat(Color color)
    {
        var mat = new StandardMaterial3D();
        mat.AlbedoColor = color;
        return mat;
    }
}
