using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class EndlessTerrain : MonoBehaviour
{
    const float scale = 2f;
    const float viewerMoveThresholdForChunkUpdate = 25f;
    const float sqrViewerMoveThresholdForChunkUpdate = viewerMoveThresholdForChunkUpdate * viewerMoveThresholdForChunkUpdate;

    public LODInfo[] detailLevels;
    public static float maxViewDst;

    [Header("Terrain Configuration")]
    public Transform viewer;
    public TerrainRegion[] terrainRegions;

    [Header("Water Configuration")]
    public bool generateWater = true;
    public Material waterMaterial;
    public LayerMask waterLayer = 4; // Layer "Water" par défaut
    public float waterLevel = 0.3f; // Hauteur en dessous de laquelle l'eau apparaît

    public static Vector2 viewerPosition;
    Vector2 viewerPositionOld;
    static MapGenerator mapGenerator;
    int chunkSize;
    int chunksVisibleInViewDst;

    Dictionary<Vector2, TerrainChunk> terrainChunkDictionary = new Dictionary<Vector2, TerrainChunk>();
    static List<TerrainChunk> terrainChunksVisibleLastUpdate = new List<TerrainChunk>();

    private void Start()
    {
        mapGenerator = FindObjectOfType<MapGenerator>();

        maxViewDst = detailLevels[detailLevels.Length - 1].visibleDstThreshold;
        chunkSize = MapGenerator.mapChunkSize - 1;
        chunksVisibleInViewDst = Mathf.RoundToInt(maxViewDst / chunkSize);

        UpdateVisibleChunks();
    }

    private void Update()
    {
        viewerPosition = new Vector2(viewer.position.x, viewer.position.z) / 2f;

        if ((viewerPositionOld - viewerPosition).sqrMagnitude > sqrViewerMoveThresholdForChunkUpdate)
        {
            viewerPositionOld = viewerPosition;
            UpdateVisibleChunks();
        }
    }

    void UpdateVisibleChunks()
    {
        for (int i = 0; i < terrainChunksVisibleLastUpdate.Count; i++)
        {
            terrainChunksVisibleLastUpdate[i].SetVisible(false);
        }
        terrainChunksVisibleLastUpdate.Clear();

        int currentChunkCoordX = Mathf.RoundToInt(viewerPosition.x / chunkSize);
        int currentChunkCoordY = Mathf.RoundToInt(viewerPosition.y / chunkSize);

        for (int yOffset = -chunksVisibleInViewDst; yOffset <= chunksVisibleInViewDst; yOffset++)
        {
            for (int xOffset = -chunksVisibleInViewDst; xOffset <= chunksVisibleInViewDst; xOffset++)
            {
                Vector2 viewedChunkCoord = new Vector2(currentChunkCoordX + xOffset, currentChunkCoordY + yOffset);

                if (terrainChunkDictionary.ContainsKey(viewedChunkCoord))
                {
                    terrainChunkDictionary[viewedChunkCoord].UpdateTerrainChunk();
                }
                else
                {
                    terrainChunkDictionary.Add(viewedChunkCoord, new TerrainChunk(viewedChunkCoord, chunkSize, detailLevels, terrainRegions, transform, generateWater, waterMaterial, waterLayer, waterLevel));
                }
            }
        }
    }

    public class TerrainChunk
    {
        GameObject meshObject;
        GameObject waterObject;
        Vector2 position;
        Bounds bounds;

        MeshRenderer meshRenderer;
        MeshFilter meshFilter;
        MeshCollider meshCollider;

        // Composants pour l'eau
        MeshRenderer waterMeshRenderer;
        MeshFilter waterMeshFilter;
        MeshCollider waterMeshCollider;

        LODInfo[] detailsLevels;
        LODMesh[] lodMeshes;
        LODMesh collisionLODMesh;
        TerrainRegion[] terrainRegions;

        bool generateWater;
        Material waterMaterial;
        LayerMask waterLayer;
        float waterLevel;

        MapData mapData;
        bool mapDataReceived;
        int previousLODIndex = -1;

        public TerrainChunk(Vector2 coord, int size, LODInfo[] detailsLevels, TerrainRegion[] terrainRegions, Transform parent, bool generateWater, Material waterMaterial, LayerMask waterLayer, float waterLevel)
        {
            this.detailsLevels = detailsLevels;
            this.terrainRegions = terrainRegions;
            this.generateWater = generateWater;
            this.waterMaterial = waterMaterial;
            this.waterLayer = waterLayer;
            this.waterLevel = waterLevel;

            position = coord * size;
            bounds = new Bounds(position, Vector2.one * size);
            Vector3 positionV3 = new Vector3(position.x, 0, position.y);

            // Création du terrain
            meshObject = new GameObject("Terrain Chunk");
            meshRenderer = meshObject.AddComponent<MeshRenderer>();
            meshFilter = meshObject.AddComponent<MeshFilter>();
            meshCollider = meshObject.AddComponent<MeshCollider>();

            meshObject.transform.position = positionV3 * scale;
            meshObject.transform.parent = parent;
            meshObject.transform.localScale = Vector3.one * scale;

            // Création de l'eau si activée
            if (generateWater && waterMaterial != null)
            {
                waterObject = new GameObject("Water Chunk");
                waterMeshRenderer = waterObject.AddComponent<MeshRenderer>();
                waterMeshFilter = waterObject.AddComponent<MeshFilter>();
                waterMeshCollider = waterObject.AddComponent<MeshCollider>();

                waterMeshRenderer.material = waterMaterial;
                waterObject.layer = (int)Mathf.Log(waterLayer.value, 2);

                waterObject.transform.position = positionV3 * scale;
                waterObject.transform.parent = parent;
                waterObject.transform.localScale = Vector3.one * scale;
            }

            SetVisible(false);

            lodMeshes = new LODMesh[detailsLevels.Length];
            for (int i = 0; i < detailsLevels.Length; i++)
            {
                lodMeshes[i] = new LODMesh(detailsLevels[i].lod, UpdateTerrainChunk);
                if (detailsLevels[i].useForCollider)
                {
                    collisionLODMesh = lodMeshes[i];
                }
            }

            mapGenerator.RequestMapData(position, OnMapDataReceived);
        }

        void OnMapDataReceived(MapData mapData)
        {
            this.mapData = mapData;
            mapDataReceived = true;

            // Application des materials basés sur la hauteur
            ApplyTerrainMaterials(mapData);

            // Génération de l'eau si nécessaire
            if (generateWater && waterObject != null)
            {
                GenerateWaterMesh(mapData);
            }

            UpdateTerrainChunk();
        }

        void ApplyTerrainMaterials(MapData mapData)
        {
            // Trouve le material dominant pour ce chunk
            Material dominantMaterial = GetDominantMaterial(mapData);
            int dominantLayer = GetDominantLayer(mapData);

            if (dominantMaterial != null)
            {
                meshRenderer.material = dominantMaterial;
            }

            meshObject.layer = dominantLayer;
        }

        Material GetDominantMaterial(MapData mapData)
        {
            if (terrainRegions == null || terrainRegions.Length == 0) return null;

            Dictionary<Material, int> materialCount = new Dictionary<Material, int>();

            for (int y = 0; y < mapData.heightMap.GetLength(1); y++)
            {
                for (int x = 0; x < mapData.heightMap.GetLength(0); x++)
                {
                    float currentHeight = mapData.heightMap[x, y];

                    for (int i = 0; i < terrainRegions.Length; i++)
                    {
                        if (currentHeight >= terrainRegions[i].height)
                        {
                            Material mat = terrainRegions[i].material;
                            if (mat != null)
                            {
                                if (materialCount.ContainsKey(mat))
                                    materialCount[mat]++;
                                else
                                    materialCount[mat] = 1;
                            }
                            break;
                        }
                    }
                }
            }

            Material dominantMaterial = null;
            int maxCount = 0;
            foreach (var kvp in materialCount)
            {
                if (kvp.Value > maxCount)
                {
                    maxCount = kvp.Value;
                    dominantMaterial = kvp.Key;
                }
            }

            return dominantMaterial;
        }

        int GetDominantLayer(MapData mapData)
        {
            if (terrainRegions == null || terrainRegions.Length == 0) return 0;

            Dictionary<int, int> layerCount = new Dictionary<int, int>();

            for (int y = 0; y < mapData.heightMap.GetLength(1); y++)
            {
                for (int x = 0; x < mapData.heightMap.GetLength(0); x++)
                {
                    float currentHeight = mapData.heightMap[x, y];

                    for (int i = 0; i < terrainRegions.Length; i++)
                    {
                        if (currentHeight >= terrainRegions[i].height)
                        {
                            int layer = (int)Mathf.Log(terrainRegions[i].layer.value, 2);
                            if (layerCount.ContainsKey(layer))
                                layerCount[layer]++;
                            else
                                layerCount[layer] = 1;
                            break;
                        }
                    }
                }
            }

            int dominantLayer = 0;
            int maxCount = 0;
            foreach (var kvp in layerCount)
            {
                if (kvp.Value > maxCount)
                {
                    maxCount = kvp.Value;
                    dominantLayer = kvp.Key;
                }
            }

            return dominantLayer;
        }

        void GenerateWaterMesh(MapData mapData)
        {
            // Crée un plan d'eau simple à la hauteur spécifiée
            List<Vector3> vertices = new List<Vector3>();
            List<int> triangles = new List<int>();
            List<Vector2> uvs = new List<Vector2>();

            bool hasWater = false;

            // Vérifie s'il y a des zones assez basses pour avoir de l'eau
            for (int y = 0; y < mapData.heightMap.GetLength(1); y++)
            {
                for (int x = 0; x < mapData.heightMap.GetLength(0); x++)
                {
                    if (mapData.heightMap[x, y] <= waterLevel)
                    {
                        hasWater = true;
                        break;
                    }
                }
                if (hasWater) break;
            }

            if (hasWater)
            {
                // Crée un plan d'eau simple
                int meshSize = MapGenerator.mapChunkSize;
                float topLeftX = (meshSize - 1) / -2f;
                float topLeftZ = (meshSize - 1) / 2f;

                vertices.Add(new Vector3(topLeftX, waterLevel, topLeftZ));
                vertices.Add(new Vector3(topLeftX + meshSize - 1, waterLevel, topLeftZ));
                vertices.Add(new Vector3(topLeftX, waterLevel, topLeftZ - (meshSize - 1)));
                vertices.Add(new Vector3(topLeftX + meshSize - 1, waterLevel, topLeftZ - (meshSize - 1)));

                triangles.AddRange(new int[] { 0, 1, 2, 1, 3, 2 });

                uvs.Add(new Vector2(0, 1));
                uvs.Add(new Vector2(1, 1));
                uvs.Add(new Vector2(0, 0));
                uvs.Add(new Vector2(1, 0));

                Mesh waterMesh = new Mesh();
                waterMesh.vertices = vertices.ToArray();
                waterMesh.triangles = triangles.ToArray();
                waterMesh.uv = uvs.ToArray();
                waterMesh.RecalculateNormals();

                waterMeshFilter.mesh = waterMesh;
                waterMeshCollider.sharedMesh = waterMesh;
            }
            else
            {
                // Pas d'eau dans ce chunk
                if (waterObject != null)
                {
                    waterObject.SetActive(false);
                }
            }
        }

        public void UpdateTerrainChunk()
        {
            if (mapDataReceived)
            {
                float viewerDstFromNearestEdge = Mathf.Sqrt(bounds.SqrDistance(viewerPosition));
                bool visible = viewerDstFromNearestEdge <= maxViewDst;

                if (visible)
                {
                    int lodIndex = 0;

                    for (int i = 0; i < detailsLevels.Length - 1; i++)
                    {
                        if (viewerDstFromNearestEdge > detailsLevels[i].visibleDstThreshold)
                        {
                            lodIndex = i + 1;
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (lodIndex != previousLODIndex)
                    {
                        LODMesh lodMesh = lodMeshes[lodIndex];
                        if (lodMesh.hasMesh)
                        {
                            previousLODIndex = lodIndex;
                            meshFilter.mesh = lodMesh.mesh;
                        }
                        else if (!lodMesh.hasRequestedMesh)
                        {
                            lodMesh.RequestMesh(mapData);
                        }
                    }

                    if (lodIndex == 0)
                    {
                        if (collisionLODMesh.hasMesh)
                        {
                            meshCollider.sharedMesh = collisionLODMesh.mesh;
                        }
                        else if (!collisionLODMesh.hasRequestedMesh)
                        {
                            collisionLODMesh.RequestMesh(mapData);
                        }
                    }

                    terrainChunksVisibleLastUpdate.Add(this);
                }

                SetVisible(visible);
            }
        }

        public void SetVisible(bool visible)
        {
            meshObject.SetActive(visible);
            if (waterObject != null)
            {
                waterObject.SetActive(visible);
            }
        }

        public bool IsVisible()
        {
            return meshObject.activeSelf;
        }
    }

    class LODMesh
    {
        public Mesh mesh;
        public bool hasRequestedMesh;
        public bool hasMesh;
        int lod;
        System.Action updateCallback;

        public LODMesh(int lod, System.Action updateCallback)
        {
            this.lod = lod;
            this.updateCallback = updateCallback;
        }

        void OnMeshDataReceived(MeshData meshData)
        {
            mesh = meshData.CreateMesh();
            hasMesh = true;

            updateCallback();
        }

        public void RequestMesh(MapData mapData)
        {
            hasRequestedMesh = true;
            mapGenerator.RequestMeshData(mapData, lod, OnMeshDataReceived);
        }
    }

    [System.Serializable]
    public struct LODInfo
    {
        public int lod;
        public float visibleDstThreshold;
        public bool useForCollider;
    }

    [System.Serializable]
    public struct TerrainRegion
    {
        [Header("Terrain Settings")]
        public string name;
        public float height;

        [Header("Visual Settings")]
        public Material material;
        public LayerMask layer;

        [Header("Properties")]
        public bool walkable;
        public float friction;
    }
}