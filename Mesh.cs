// ============================================================================
//  Mesh4.cs  —  Mask ➜ Walkable FLOOR Mesh για Unity 2.5D / Adventure Creator
// ============================================================================
//  Συγχώνευση:
//    • Γεωμετρία (Floor Placement): από την v1 — κάθε mask-pixel γίνεται ray
//      μέσα από την κάμερα (ViewportPointToRay) και τέμνει το οριζόντιο
//      επίπεδο y = floorY. Αυτό είναι το μοντέλο που δούλεψε καλύτερα όταν
//      άλλαξε το FOV. maxFloorDistance/meshDistanceScale/depthCompression/
//      meshWorldOffset/targetSurfaceZ δουλεύουν όπως στην v1.
//    • Mesh generation: από την v3 — marching-squares contour trace +
//      Douglas-Peucker simplification + ear-clip triangulation. Πολύ
//      λιγότερα polygons από το παλιό "ένα quad per pixel".
//    • Αφαιρέθηκαν: Warp Grid (control points / RBF), Mesh Scaling
//      (scaleX/Y/Z, depthMultiplier), Live Preview toggle.
//    • Μία μόνο μάσκα (maskTexture) αντί για array.
//    • Gizmo modes όπως στην v3: None / WhenSelected / WhenCamera / Always.
//    • Player Spawn point (UV, draggable handle στο Scene view) + κουμπί
//      "Τοποθέτησε Player".
//    • Auto-invalidate bake: οποιαδήποτε αλλαγή παραμέτρου (ή κίνηση της
//      κάμερας/transform) ανιχνεύεται μέσω hash· αν υπάρχει baked asset,
//      διαγράφεται αυτόματα και το collider γυρίζει σε live mesh.
//
//  Setup:
//    1. Άδειο GameObject (ιδανικά layer "NavMesh").
//    2. Πρόσθεσε Mesh4 (βάζει μόνο του MeshCollider) + AC NavigationMesh.
//    3. Όρισε maskTexture (λευκό = βατό) και warpCamera.
//    4. Ρύθμισε floorY / maxFloorDistance / meshWorldOffset / targetSurfaceZ
//       ώστε το gizmo πάτωμα να συμπέσει με τη ζωγραφιά.
//    5. Bake όταν είσαι ικανοποιημένος.
// ============================================================================

using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
[RequireComponent(typeof(MeshCollider))]
[AddComponentMenu("Adventure Creator Tools/Mesh4 (Mask To Floor NavMesh)")]
public class Mesh4 : MonoBehaviour
{
    public enum GizmoMode { None, WhenSelected, WhenCamera, Always }

    /// <summary>
    /// Grid: ένα quad ανά (downsampled) pixel της μάσκας, όπως στο "Script 1" — αλλά με το
    /// ανθεκτικό unprojection του Mesh4 (καμία απόρριψη/clamp-collapse κοντά στον ορίζοντα),
    /// οπότε εγγυάται 100% κάλυψη της μάσκας. Περισσότερα πολύγωνα (ελέγχεται από downsampling).<br/>
    /// Contour: contour-trace + simplify + ear-clip — πολύ λιγότερα πολύγωνα, αλλά το
    /// Simplification "τρώει" ελαφρά τις γωνίες (το Mask Dilation το αντισταθμίζει).
    /// </summary>
    public enum MeshGenerationMode { Grid, Contour }

    /// <summary>
    /// Perspective (default, v1-style): X/Z προκύπτουν από την τομή ray↔(y=floorY). Σωστό
    /// για το ΚΟΝΤΙΝΟ τμήμα της μάσκας, αλλά το βάθος "εκρήγνυται" όσο το v πλησιάζει το
    /// ύψος-ματιού της κάμερας (v≈0.5 σε μη-tilted κάμερα) — το Max Floor Distance απλά
    /// "κόβει" αυτή την έκρηξη κάπου, χωρίς νόημα σε σχέση με hotspots.<br/>
    /// Linear: X = lerp(Left X, Right X, u), Z = lerp(Near Depth Z, Far Depth Z, v),
    /// Y = floorY. Καμία εξάρτηση από κάμερα/ray — η μάσκα τοποθετείται σαν ένα
    /// ορθογώνιο στο world XZ με τα 4 άκρα της ΑΚΡΙΒΩΣ εκεί που ορίζεις (π.χ. στα
    /// X/Z των αριστερών/δεξιών/μακρινών hotspots).
    /// </summary>
    public enum DepthMode { Perspective, Linear }

    /// <summary>
    /// Mask: το mesh παράγεται από PNG μάσκα (Grid ή Contour generation) + camera/Linear
    /// unprojection — βλ. DepthMode.<br/>
    /// Polygon: το mesh παράγεται απευθείας από μια λίστα Transform (Outline Points) που
    /// τοποθετείς γύρω-γύρω στο world space, με τη σειρά. Κάθε vertex = (point.x, floorY,
    /// point.z) — τριγωνοποιείται με EarClip. Καμία κάμερα/ray/FOV εμπλέκεται· τα άκρα του
    /// mesh ταυτίζονται ΑΚΡΙΒΩΣ με τα σημεία που έβαλες (π.χ. δίπλα σε hotspots).
    /// </summary>
    public enum MeshSourceMode { Mask, Polygon }



    // ─────────────────────────────────────────────
    // MESH SOURCE
    // ─────────────────────────────────────────────
    [Header("Mesh Source")]
    [Tooltip("Mask: παράγεται από PNG μάσκα + camera/Linear unprojection (παρακάτω πεδία).\n" +
             "Polygon: παράγεται απευθείας από τα Outline Points — flat mesh στο Y=floorY, " +
             "χωρίς κάμερα/FOV. Τα άκρα ταυτίζονται ΑΚΡΙΒΩΣ με τα σημεία που βάζεις.")]
    public MeshSourceMode meshSourceMode = MeshSourceMode.Mask;

    // ─────────────────────────────────────────────
    // POLYGON OUTLINE  (εναλλακτικό του Mask)
    // ─────────────────────────────────────────────
    [Header("Polygon Outline (αν Mesh Source = Polygon)")]
    [Tooltip("Σημεία περιγράμματος σε LOCAL space (σχετικά με αυτό το GameObject), ΜΕ ΤΗ ΣΕΙΡΑ. " +
             "Το mesh γεμίζει το εσωτερικό τους (EarClip). Πάτα 'Initialize Polygon from Mask' " +
             "για αυτόματο αρχικό περίγραμμα από τη μάσκα, μετά σύρε τα handles στο Scene view " +
             "για να το ταιριάξεις στο 3D χώρο. Το Y κάθε σημείου αγνοείται (χρησιμοποιείται floorY).")]
    public List<Vector3> outlinePoints = new List<Vector3>();

    [Range(6, 80)]
    [Tooltip("Πόσα σημεία θα παραχθούν από το 'Initialize Polygon from Mask'. " +
             "Λιγότερα = πιο εύκολο editing· περισσότερα = πιστότερο περίγραμμα.")]
    public int initResolution = 16;

    [Tooltip("Για το 'Initialize Polygon from Camera Rect': world Z του ΜΑΚΡΙΝΟΥ άκρου " +
             "του ορθογωνίου (v=1). Το κοντινό άκρο (v=0) μπαίνει στο φυσικό βάθος της " +
             "κάμερας. Default 0.")]
    public float rectFarZ = 0f;

    [Range(1, 12)]
    [Tooltip("Για το 'Initialize Polygon from Camera Rect': πόσα τμήματα ανά πλευρά " +
             "(περισσότερα = πιο πολλά handles για να 'λυγίσεις' το ορθογώνιο σε σχήμα).")]
    public int rectSubdivisions = 1;

    [Tooltip("Αν true, το Polygon mesh έχει faces ΚΑΙ από τις δύο πλευρές (double-sided), " +
             "ώστε το AC raycast να το χτυπάει σίγουρα ανεξάρτητα από προσανατολισμό. " +
             "Συνιστάται ON για NavMesh.")]
    public bool doubleSidedPolygon = true;



    // ─────────────────────────────────────────────
    // MASK
    // ─────────────────────────────────────────────
    [Header("Μάσκα (αν Mesh Source = Mask)")]
    [Tooltip("PNG μάσκα — λευκό = βατό, μαύρο = μη βατό")]
    public Texture2D maskTexture;

    [Range(0.05f, 0.95f)]
    [Tooltip("Brightness threshold (0-1) πάνω από το οποίο ένα pixel θεωρείται βατό")]
    public float threshold = 0.5f;


    // ─────────────────────────────────────────────
    // CAMERA UNPROJECT / FLOOR  (από v1 — δουλεύει καλά με FOV tuning)
    // ─────────────────────────────────────────────
    [Header("Camera Unproject / Floor")]
    [Tooltip("Η κάμερα της σκηνής. Κάθε mask-pixel προβάλλεται μέσα από αυτή στο δάπεδο.")]
    public Camera warpCamera;

    [HideInInspector]
    [Tooltip("Βοηθητικό: πού βλέπεις τον ορίζοντα/eye-level στη ζωγραφιά (0=κάτω, 1=πάνω). " +
             "Το κουμπί 'Set Camera Tilt' υπολογίζει το rotation X ώστε ο ορίζοντας της " +
             "κάμερας να πέσει εκεί. Σχεδιάζεται custom δίπλα στο Warp Camera.")]
    public float horizonV = 0.5f;

    [Tooltip("Perspective: Z από τομή ray↔floorY (σωστό κοντά, 'εκρήγνυται' κοντά στο " +
             "ύψος-ματιού της κάμερας). Linear: Z = lerp(Near/Far Depth Z, v) — χωρίς " +
             "ασύμπτωτο, ιδανικό όταν η μάσκα φτάνει ψηλά (π.χ. ως πόρτα προς άλλο δωμάτιο).")]
    public DepthMode depthMode = DepthMode.Perspective;

    [Tooltip("Y θέση του βατού επιπέδου στο world space")]
    public float floorY = -2.72f;

    [Tooltip("Μόνο για Perspective mode. Μέγιστη απόσταση (world units, σε βάθος Z) από " +
             "την κάμερα που επιτρέπεται να προσγειωθεί ένα floor pixel. Πέρα από αυτό, " +
             "το pixel 'κόβεται' σε σταθερό Z = camera.z ± Max Floor Distance (Y=floorY) " +
             "— δεν πετιέται, αλλά η θέση εκεί είναι αυθαίρετη ως προς hotspots.")]
    public float maxFloorDistance = 50f;

    [Tooltip("Μόνο για Linear mode. World X στο u=0 (αριστερό άκρο της μάσκας). " +
             "Βάλε το Transform.X του αριστερού hotspot.")]
    public float leftX = -10f;

    [Tooltip("Μόνο για Linear mode. World X στο u=1 (δεξί άκρο της μάσκας). " +
             "Βάλε το Transform.X του δεξιού hotspot.")]
    public float rightX = 10f;

    [Tooltip("Μόνο για Linear mode. World Z στο v=0 (κάτω/κοντινό άκρο της μάσκας). " +
             "Βάλε ίδια τιμή με το Z που ήδη δουλεύει καλά στο κοντινό τμήμα " +
             "(δες Log Depth Range στο Perspective mode πριν αλλάξεις).")]
    public float nearDepthZ = 0f;

    [Tooltip("Μόνο για Linear mode. World Z στο v=1 (πάνω/μακρινό άκρο της μάσκας). " +
             "Βάλε το Z όπου θέλεις να 'ακουμπάει' το μακρινό τμήμα — π.χ. το Transform.Z " +
             "ενός μακρινού hotspot (Bedroom/Exit).")]
    public float farDepthZ = 30f;


    [Range(0.1f, 1f)]
    [Tooltip("Αγνόησε mask pixels πάνω από αυτό το v (κόψε την περιοχή κοντά στον ορίζοντα). 1 = κανένα όριο.")]
    public float horizonClampV = 1f;

    [Range(0.05f, 1f)]
    [Tooltip("Κλιμακώνει ολόκληρο το walkable ΠΡΟΣ τη θέση της κάμερας. " +
             "1 = ακριβές φυσικό δάπεδο. Μικρότερο = πιο κοντά στην κάμερα.")]
    public float meshDistanceScale = 1f;

    [Range(0.05f, 1f)]
    [Tooltip("Συμπιέζει ΜΟΝΟ το βάθος (Z) γύρω από το Target Surface Z, κρατώντας το X/Y σταθερό.")]
    public float depthCompression = 1f;

    [Tooltip("Χειροκίνητη μετατόπιση (world units) που εφαρμόζεται σε κάθε regen, " +
             "ΜΕΤΑ το unprojection.")]
    public Vector3 meshWorldOffset = Vector3.zero;

    [Tooltip("Target world Z για το κέντρο της επιφάνειας — βλ. κουμπί 'Snap Surface to Z'.")]
    public float targetSurfaceZ = -5f;

    [Tooltip("Αν true, καταγράφει στο Console το εύρος βάθους (Z) του mesh μετά το Generate.")]
    public bool logDepthRange = false;

    // ─────────────────────────────────────────────
    // MESH GENERATION
    // ─────────────────────────────────────────────
    [Header("Mesh Generation")]
    [Tooltip("Grid (default): ένα quad ανά (downsampled) pixel της μάσκας — όπως το Script 1, " +
             "αλλά με το ανθεκτικό unprojection του Mesh4 (κανένα pixel δεν απορρίπτεται/" +
             "κολλάει κοντά στον ορίζοντα), οπότε εγγυάται 100% κάλυψη της μάσκας.\n" +
             "Contour: contour-trace + simplify + ear-clip — πολύ λιγότερα πολύγωνα, αλλά το " +
             "Simplification 'τρώει' ελαφρά τις γωνίες (το Mask Dilation το αντισταθμίζει).")]
    public MeshGenerationMode meshGenerationMode = MeshGenerationMode.Grid;

    [Range(1, 20)]
    [Tooltip("Μόνο για Grid mode: συρρίκνωση μάσκας πριν το grid (π.χ. 8 = 1 quad ανά 8x8 px). " +
             "Μικρότερο = πιο ακριβές περίγραμμα αλλά πολλαπλάσια πολύγωνα/vertices.")]
    public int downsampling = 8;

    [Range(0, 20)]
    [Tooltip("Διαστέλλει (dilate) τη λευκή/βατή περιοχή της μάσκας κατά Ν pixels ΠΡΙΝ τη " +
             "δημιουργία mesh, ώστε το collider να είναι πάντα ελαφρώς ΜΕΓΑΛΥΤΕΡΟ από τη " +
             "ζωγραφισμένη βατή περιοχή.")]
    public int maskDilation = 2;

    [Range(0f, 30f)]
    [Tooltip("Contour mode: Douglas-Peucker simplification σε pixels (2-4 ιδανικό).\n" +
             "Grid mode: μέγιστο μήκος horizontal run — ενώνει συνεχόμενα walkable cells " +
             "της ίδιας γραμμής σε ένα φαρδύ quad (λιγότερα τρίγωνα, ίδια κάλυψη). " +
             "1 = ένα quad/cell, μεγαλύτερο = λιγότερα/φαρδύτερα quads.\n" +
             "Polygon mode: αγνοείται (το mesh ορίζεται από τα Outline Points).")]
    public float simplification = 2f;

    [Min(0)]
    [Tooltip("Μόνο για Contour mode: αγνόησε νησίδες/τρύπες μικρότερες από αυτό το εμβαδόν (px²)")]
    public int minRegionArea = 100;

    public bool removeOriginalBoxCollider = true;

    // ─────────────────────────────────────────────
    // BAKE
    // ─────────────────────────────────────────────
    [Header("Bake")]
    [Tooltip("Αν υπάρχει, ο MeshCollider το χρησιμοποιεί αυτόματα. " +
             "Οποιαδήποτε αλλαγή παραμέτρου το διαγράφει αυτόματα και γυρίζει σε live mesh.")]
    public Mesh bakedMeshAsset;

    [Tooltip("Αν ON και υπάρχει baked asset: κάθε αλλαγή κάνει RE-BAKE επιτόπου στο ίδιο " +
             "asset (overwrite), αντί να διαγράφει το bake και να γυρνά σε live. Έτσι το " +
             "mesh μένει ΠΑΝΤΑ baked και δεν χάνεις το asset. (OFF = παλιά συμπεριφορά: " +
             "αλλαγή ➜ clear bake ➜ live.)")]
    public bool autoReBake = true;

    // ─────────────────────────────────────────────
    // GIZMOS
    // ─────────────────────────────────────────────
    [Header("Gizmos")]
    public GizmoMode gizmoMode = GizmoMode.WhenSelected;
    public Color fillColor = new Color(0f, 1f, 0.4f, 0.25f);
    public Color wireColor = new Color(0f, 1f, 0.4f, 0.9f);

    [Tooltip("Δείχνει δύο πορτοκαλί πλαίσια στο Z = camera.z ± Max Floor Distance. " +
             "Βοηθάει να δεις ΟΠΤΙΚΑ αν το far/πάνω τμήμα του mesh 'κόβεται' (clamp) πριν " +
             "φτάσει στο σωστό βάθος — π.χ. σύγκρινέ το με τη θέση των Bedroom/Exit hotspots.")]
    public bool showDepthLimitGizmo = false;

    // ─────────────────────────────────────────────
    // PLAYER SPAWN
    // ─────────────────────────────────────────────
    [Header("Player Spawn")]
    [Tooltip("Μόνο για Mesh Source = Mask. Σημείο εισόδου σε mask-space (0-1). " +
             "x=αριστερά→δεξιά, y=κάτω→πάνω. Προεπιλογή: κέντρο. Κουμπί " +
             "'Spawn ➜ Κέντρο Mesh' το υπολογίζει αυτόματα.")]
    public Vector2 playerSpawnUV = new Vector2(0.5f, 0.5f);

    [Tooltip("Μόνο για Mesh Source = Polygon. World θέση spawn (X,Z· το Y αγνοείται, " +
             "χρησιμοποιείται floorY). Σύρε τον κίτρινο δείκτη στο Scene view.")]
    public Vector3 polygonSpawnPoint = Vector3.zero;

    [Tooltip("Αν κενό, ψάχνει tag 'Player'")]
    public Transform playerTransform;

    // ─────────────────────────────────────────────
    // INTERNALS
    // ─────────────────────────────────────────────
    [System.NonSerialized] public int statVerts;
    [System.NonSerialized] public int statTris;

    Mesh liveMesh;
    MeshCollider meshCollider;
    int lastHash;

    // ═════════════════════════════════════════════
    // UNITY LIFECYCLE
    // ═════════════════════════════════════════════

    void Reset()
    {
        if (warpCamera == null) warpCamera = Camera.main;
        int navLayer = LayerMask.NameToLayer("NavMesh");
        if (navLayer >= 0) gameObject.layer = navLayer;
#if UNITY_EDITOR
        // Deferred: το AddComponent μέσα στο Reset() μπλοκάρεται από τον editor σε ορισμένες εκδόσεις του Unity.
        EditorApplication.delayCall += () =>
        {
            if (this == null) return;
            SetupACComponents();
        };
#endif
    }

    void OnEnable()
    {
        meshCollider = GetComponent<MeshCollider>();
        if (removeOriginalBoxCollider) RemoveBoxCollider();
        Refresh();
        lastHash = ComputeHash();
    }

    void OnDestroy()
    {
        if (liveMesh != null) DestroySafe(liveMesh);
    }

    void OnValidate()
    {
        horizonClampV = Mathf.Clamp(horizonClampV, 0.1f, 1f);
        playerSpawnUV.x = Mathf.Clamp01(playerSpawnUV.x);
        playerSpawnUV.y = Mathf.Clamp01(playerSpawnUV.y);
    }

    void Update()
    {
#if UNITY_EDITOR
        if (Application.isPlaying) return;
        int h = ComputeHash();
        if (h != lastHash)
        {
            lastHash = h;
            if (bakedMeshAsset != null)
            {
                if (autoReBake) BakeToAsset();     // re-bake επιτόπου, κράτα το asset
                else { ClearBakedAsset(true); RegenerateLive(); }
            }
            else
            {
                RegenerateLive();
            }
            SceneView.RepaintAll();
        }
#endif
    }

    int ComputeHash()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + (int)meshSourceMode;
            if (meshSourceMode == MeshSourceMode.Polygon)
            {
                if (outlinePoints != null)
                    foreach (var p in outlinePoints)
                        h = h * 31 + p.GetHashCode();
                h = h * 31 + (doubleSidedPolygon ? 1 : 0);
                h = h * 31 + polygonSpawnPoint.GetHashCode();
                h = h * 31 + floorY.GetHashCode();
                h = h * 31 + transform.position.GetHashCode();
                h = h * 31 + transform.rotation.GetHashCode();
                h = h * 31 + transform.lossyScale.GetHashCode();
                return h;
            }
            h = h * 31 + (maskTexture != null ? maskTexture.GetEntityId().GetHashCode() : 0);
            h = h * 31 + threshold.GetHashCode();
            h = h * 31 + (int)meshGenerationMode;
            h = h * 31 + downsampling;
            h = h * 31 + (int)depthMode;
            h = h * 31 + leftX.GetHashCode();
            h = h * 31 + rightX.GetHashCode();
            h = h * 31 + nearDepthZ.GetHashCode();
            h = h * 31 + farDepthZ.GetHashCode();
            h = h * 31 + floorY.GetHashCode();
            h = h * 31 + maxFloorDistance.GetHashCode();
            h = h * 31 + horizonClampV.GetHashCode();
            h = h * 31 + meshDistanceScale.GetHashCode();
            h = h * 31 + depthCompression.GetHashCode();
            h = h * 31 + meshWorldOffset.GetHashCode();
            h = h * 31 + targetSurfaceZ.GetHashCode();
            h = h * 31 + simplification.GetHashCode();
            h = h * 31 + minRegionArea;
            h = h * 31 + maskDilation;
            if (warpCamera != null)
            {
                h = h * 31 + warpCamera.transform.position.GetHashCode();
                h = h * 31 + warpCamera.transform.rotation.GetHashCode();
                h = h * 31 + warpCamera.fieldOfView.GetHashCode();
                h = h * 31 + warpCamera.aspect.GetHashCode();
                h = h * 31 + (warpCamera.orthographic ? 1 : 0);
                h = h * 31 + warpCamera.orthographicSize.GetHashCode();
            }
            h = h * 31 + transform.position.GetHashCode();
            h = h * 31 + transform.rotation.GetHashCode();
            h = h * 31 + transform.lossyScale.GetHashCode();
            return h;
        }
    }

    // ═════════════════════════════════════════════
    // FLOOR PLACEMENT  (camera unproject — από v1)
    // ═════════════════════════════════════════════

    /// <summary>
    /// Viewport UV (0-1, v=0 κάτω) ➜ world θέση.<br/>
    /// <b>Perspective</b>: ray μέσα από την κάμερα, τομή με y=floorY. Σωστό κοντά στην
    /// κάμερα, αλλά το βάθος "εκρήγνυται" όσο το v πλησιάζει το ύψος-ματιού της κάμερας
    /// (όπου η ακτίνα γίνεται παράλληλη στο δάπεδο). Πέρα από maxFloorDistance σε
    /// βάθος Z, ΔΕΝ ακολουθεί την ακτίνα μέχρι εκεί (αυτό την πάει σε ύψος κοντά στην
    /// κάμερα → κάθετο "πτερύγιο"). Αντί γι' αυτό προβάλλεται μόνο το X μέσω της
    /// αναλογίας X/Z της ακτίνας πάνω σε σταθερό Z = camera.z ± maxFloorDistance, με
    /// Y=floorY — ένα οριζόντιο "πίσω άκρο", όχι τοίχος. Η θέση εκεί όμως είναι
    /// αυθαίρετη ως προς hotspots.<br/>
    /// <b>Linear</b>: X = lerp(leftX, rightX, u), Z = lerp(nearDepthZ, farDepthZ, v),
    /// Y = floorY. Καμία εξάρτηση από την κάμερα/ray — η μάσκα γίνεται ένα ορθογώνιο
    /// στο world XZ με τα 4 άκρα του ΑΚΡΙΒΩΣ εκεί που ορίζεις (π.χ. στα X/Z των
    /// αριστερών/δεξιών/μακρινών hotspots).
    /// </summary>
    public Vector3 UnprojectViewportToWorld(Vector2 uv)
    {
        if (warpCamera == null) return transform.position;

        Vector3 camPos = warpCamera.transform.position;
        Vector3 worldHit;

        if (depthMode == DepthMode.Linear)
        {
            float x = Mathf.LerpUnclamped(leftX, rightX, uv.x);
            float z = Mathf.LerpUnclamped(nearDepthZ, farDepthZ, uv.y);
            worldHit = new Vector3(x, floorY, z);
        }
        else
        {
            Ray ray = warpCamera.ViewportPointToRay(new Vector3(uv.x, uv.y, 0f));
            Plane floorPlane = new Plane(Vector3.up, new Vector3(0f, floorY, 0f));
            bool gotFloorHit = floorPlane.Raycast(ray, out float dist) && dist > 0f;
            if (gotFloorHit)
            {
                worldHit = ray.GetPoint(dist);
                float dz = worldHit.z - camPos.z;
                if (Mathf.Abs(dz) > maxFloorDistance)
                    worldHit = ProjectRayToZ(ray, camPos.z + Mathf.Sign(dz) * maxFloorDistance);
            }
            else
            {
                float sign = !Mathf.Approximately(ray.direction.z, 0f) ? Mathf.Sign(ray.direction.z) : 1f;
                worldHit = ProjectRayToZ(ray, camPos.z + sign * maxFloorDistance);
            }
        }

        // Κλιμάκωση προς τη θέση της κάμερας κατά μήκος της ακτίνας.
        if (meshDistanceScale < 0.999f)
            worldHit = camPos + meshDistanceScale * (worldHit - camPos);

        // Συμπίεση βάθους γύρω από το targetSurfaceZ.
        if (depthCompression < 0.999f)
            worldHit.z = targetSurfaceZ + depthCompression * (worldHit.z - targetSurfaceZ);

        worldHit += meshWorldOffset;
        return worldHit;
    }

    /// <summary>
    /// Προβάλλει την ακτίνα σε σταθερό βάθος world Z: υπολογίζει το X μέσω της
    /// αναλογίας X/Z της κατεύθυνσης (ίδιο τρίγωνο με την τομή ray↔floorY, απλά με
    /// τον άξονα Z ως περιορισμό αντί για Y), και θέτει Y=floorY. Χρησιμοποιείται
    /// τόσο για το depth-cap του Perspective mode όσο και για ΚΑΘΕ σημείο στο
    /// Linear mode.
    /// </summary>
    Vector3 ProjectRayToZ(Ray ray, float targetZ)
    {
        float x = ray.origin.x;
        if (Mathf.Abs(ray.direction.z) > 1e-6f)
            x = ray.origin.x + ray.direction.x / ray.direction.z * (targetZ - ray.origin.z);
        return new Vector3(x, floorY, targetZ);
    }


    public Vector3 UnprojectViewportToLocal(Vector2 uv) =>
        transform.InverseTransformPoint(UnprojectViewportToWorld(uv));

    /// <summary>world ➜ viewport UV (camera projection, για το spawn handle).</summary>
    public Vector2 WorldToUV(Vector3 world)
    {
        if (warpCamera == null) return new Vector2(0.5f, 0.25f);
        Vector3 vp = warpCamera.WorldToViewportPoint(world);
        return new Vector2(vp.x, vp.y);
    }

    // ═════════════════════════════════════════════
    // SNAP SURFACE TO Z  (από v1)
    // ═════════════════════════════════════════════

    public void SnapSurfaceToZ()
    {
        if (warpCamera == null)
        {
            Debug.LogWarning("[Mesh4] Snap Surface to Z χρειάζεται warp camera.");
            return;
        }

        Mesh m = GetActiveMesh();
        if (m == null || m.vertexCount == 0)
        {
            RegenerateLive();
            m = liveMesh;
        }
        if (m == null || m.vertexCount == 0)
        {
            Debug.LogWarning("[Mesh4] Δεν υπάρχει mesh να γίνει snap — generate πρώτα.");
            return;
        }

        var verts = m.vertices;
        double sumZ = 0;
        for (int i = 0; i < verts.Length; i++)
            sumZ += transform.TransformPoint(verts[i]).z;
        float centroidZ = (float)(sumZ / verts.Length);

        float delta = targetSurfaceZ - centroidZ;
        meshWorldOffset.z += delta;

        Debug.Log($"[Mesh4] Snap surface: centroid Z {centroidZ:F2} → {targetSurfaceZ:F2} (Δz {delta:+0.00;-0.00}).");

#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
        if (bakedMeshAsset != null)
        {
            if (autoReBake) { BakeToAsset(); return; }
            ClearBakedAsset(true);
        }
#endif
        RegenerateLive();
    }

    // ═════════════════════════════════════════════
    // SPAWN POINT
    // ═════════════════════════════════════════════

    public Vector3 GetSpawnWorldPosition()
    {
        if (meshSourceMode == MeshSourceMode.Polygon)
            return new Vector3(polygonSpawnPoint.x, floorY, polygonSpawnPoint.z);
        return UnprojectViewportToWorld(playerSpawnUV);
    }

    /// <summary>
    /// Mask mode: υπολογίζει το (area-weighted) κεντροειδές της μεγαλύτερης βατής
    /// περιοχής της μάσκας και τοποθετεί εκεί το playerSpawnUV.<br/>
    /// Polygon mode: υπολογίζει το κεντροειδές των Outline Points και τοποθετεί
    /// εκεί το polygonSpawnPoint.
    /// </summary>
    public void CenterSpawnOnMesh()
    {
        if (meshSourceMode == MeshSourceMode.Polygon)
        {
            if (outlinePoints == null || outlinePoints.Count < 3)
            {
                Debug.LogWarning("[Mesh4] Polygon mode: χρειάζονται τουλάχιστον 3 Outline Points.");
                return;
            }
            var poly = new List<Vector2>(outlinePoints.Count);
            foreach (var p in outlinePoints)
            {
                Vector3 wp = transform.TransformPoint(p);
                poly.Add(new Vector2(wp.x, wp.z));
            }
            if (poly.Count < 3) return;

            Vector2 c = PolygonCentroid(poly);
#if UNITY_EDITOR
            Undo.RecordObject(this, "Center Spawn On Mesh");
#endif
            polygonSpawnPoint = new Vector3(c.x, floorY, c.y);
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
            Debug.Log($"[Mesh4] Spawn ➜ κέντρο polygon, world ({c.x:0.##}, {floorY:0.##}, {c.y:0.##}).");
            return;
        }

        if (!ComputeWalkablePolygons(out var outers, out _, out int w, out int h))
        {
            Debug.LogWarning("[Mesh4] Δεν βρέθηκε βατή περιοχή — δεν μπόρεσα να υπολογίσω κέντρο.");
            return;
        }

        List<Vector2> best = null;
        float bestArea = -1f;
        foreach (var o in outers)
        {
            float a = Mathf.Abs(SignedArea(o));
            if (a > bestArea) { bestArea = a; best = o; }
        }
        if (best == null) return;

        Vector2 c2 = PolygonCentroid(best);
        Vector2 uv = new Vector2(Mathf.Clamp01(c2.x / w), Mathf.Clamp01(c2.y / h));

#if UNITY_EDITOR
        Undo.RecordObject(this, "Center Spawn On Mesh");
#endif
        playerSpawnUV = uv;
#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
#endif
        Debug.Log($"[Mesh4] Spawn ➜ κέντρο mesh, UV ({uv.x:0.##}, {uv.y:0.##}).");
    }

    static Vector2 PolygonCentroid(List<Vector2> poly)
    {
        float sixA = 0f, cx = 0f, cy = 0f;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 p0 = poly[i], p1 = poly[(i + 1) % poly.Count];
            float cross = p0.x * p1.y - p1.x * p0.y;
            sixA += cross;
            cx += (p0.x + p1.x) * cross;
            cy += (p0.y + p1.y) * cross;
        }
        sixA *= 3f; // 6 * signed area
        if (Mathf.Abs(sixA) < 1e-9f)
        {
            Vector2 avg = Vector2.zero;
            foreach (var p in poly) avg += p;
            return avg / poly.Count;
        }
        return new Vector2(cx / sixA, cy / sixA);
    }

    public void PlacePlayerAtSpawn()
    {
        Transform t = playerTransform;
        if (t == null)
        {
            GameObject p = GameObject.FindWithTag("Player");
            if (p != null) t = p.transform;
        }
        if (t == null)
        {
            Debug.LogWarning("[Mesh4] Δεν βρέθηκε Player (όρισε playerTransform ή tag 'Player').");
            return;
        }
#if UNITY_EDITOR
        Undo.RecordObject(t, "Place Player At Spawn");
#endif
        t.position = GetSpawnWorldPosition();
#if UNITY_EDITOR
        EditorUtility.SetDirty(t);
#endif
        Debug.Log("[Mesh4] Player ➜ " + t.position);
    }

    // ═════════════════════════════════════════════
    // HORIZON ➜ CAMERA TILT  (helper για νέα backgrounds)
    // ═════════════════════════════════════════════

    /// <summary>
    /// Προτεινόμενο rotation X (μοίρες, κοιτάει κάτω = θετικό) ώστε ο ορίζοντας
    /// της κάμερας να πέσει στο horizonV της ζωγραφιάς.
    /// Σε μια κάμερα με pitch p (κάτω θετικό), ο ορίζοντας (όπου η ακτίνα είναι
    /// οριζόντια) εμφανίζεται στο viewport v = 0.5 + p/FOV_v (γραμμικά γύρω από το
    /// κέντρο). Λύνοντας ως προς p: p = (horizonV - 0.5) * FOV_v.
    /// Αν horizonV > 0.5 (ορίζοντας ψηλά → βλέπεις πολύ πάτωμα), p > 0 → κοιτάει κάτω.
    /// </summary>
    public float SuggestedTiltX()
    {
        float fovV = (warpCamera != null && !warpCamera.orthographic) ? warpCamera.fieldOfView : 45f;
        return (horizonV - 0.5f) * fovV;
    }

    public void ApplyHorizonTilt()
    {
        if (warpCamera == null)
        {
            Debug.LogWarning("[Mesh4] Set Camera Tilt: όρισε warpCamera.");
            return;
        }
        float tilt = SuggestedTiltX();
#if UNITY_EDITOR
        Undo.RecordObject(warpCamera.transform, "Set Camera Tilt From Horizon");
#endif
        Vector3 e = warpCamera.transform.eulerAngles;
        e.x = tilt;
        warpCamera.transform.eulerAngles = e;
#if UNITY_EDITOR
        EditorUtility.SetDirty(warpCamera.transform);
#endif
        Debug.Log($"[Mesh4] Camera rotation X ➜ {tilt:0.##}° (horizon v={horizonV:0.##}, FOV={warpCamera.fieldOfView:0.#}). " +
                  "Κούρδισε floorY/FOV αν χρειάζεται.");
        RegenerateLive();
    }

    // ═════════════════════════════════════════════
    // REFRESH / MESH ASSIGNMENT
    // ═════════════════════════════════════════════

    public void Refresh()
    {
        if (meshCollider == null) meshCollider = GetComponent<MeshCollider>();
        if (bakedMeshAsset != null) { AssignMesh(bakedMeshAsset); return; }
        RegenerateLive();
    }

    public void RegenerateLive()
    {
        Mesh m = GenerateCollisionMesh();
        if (m == null) return;
        if (liveMesh != null) DestroySafe(liveMesh);
        liveMesh = m;
        liveMesh.hideFlags = HideFlags.DontSave;
        AssignMesh(liveMesh);
    }

    void AssignMesh(Mesh m)
    {
        if (meshCollider == null) meshCollider = GetComponent<MeshCollider>();
        if (meshCollider != null)
        {
            meshCollider.sharedMesh = null;
            meshCollider.convex = false;
            meshCollider.sharedMesh = m;
        }
        statVerts = (m != null) ? m.vertexCount : 0;
        statTris = (m != null) ? m.triangles.Length / 3 : 0;
    }

    public Mesh GetActiveMesh()
    {
        if (bakedMeshAsset != null) return bakedMeshAsset;
        if (liveMesh != null) return liveMesh;
        if (meshCollider == null) meshCollider = GetComponent<MeshCollider>();
        return meshCollider != null ? meshCollider.sharedMesh : null;
    }

    void RemoveBoxCollider()
    {
        var bc = GetComponent<BoxCollider>();
        if (bc == null) return;
#if UNITY_EDITOR
        DestroyImmediate(bc);
#else
        Destroy(bc);
#endif
        var mr = GetComponent<MeshRenderer>();
        if (mr != null) mr.enabled = false;
    }

    static void DestroySafe(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
    }

    // ═════════════════════════════════════════════
    // MESH GENERATION  (contour trace + simplify + ear-clip — από v3,
    // αλλά τα vertices έρχονται από UnprojectViewportToWorld — v1 γεωμετρία)
    // ═════════════════════════════════════════════

    /// <summary>
    /// Μάσκα ➜ threshold ➜ dilation ➜ horizon clamp ➜ contour trace ➜ simplify ➜
    /// ταξινόμηση σε outer/hole loops (mask-pixel space). Χρησιμοποιείται και
    /// από το GenerateCollisionMesh και από το CenterSpawnOnMesh.
    /// </summary>
    bool ComputeWalkablePolygons(out List<List<Vector2>> outers, out List<List<Vector2>> holes, out int w, out int h)
    {
        outers = null; holes = null; w = 0; h = 0;
        if (maskTexture == null) return false;

        Color32[] pixels = ReadPixels(maskTexture, out w, out h);
        if (pixels == null || pixels.Length != w * h) return false;

        bool[] solid = new bool[w * h];
        int thr = Mathf.RoundToInt(threshold * 255f);
        for (int i = 0; i < solid.Length; i++)
        {
            Color32 c = pixels[i];
            solid[i] = (c.r + c.g + c.b) / 3 > thr;
        }

        // Dilate the walkable area by N pixels BEFORE simplification, so the
        // collider mesh is always slightly LARGER than the painted floor —
        // this prevents the player from falling through at the edges once
        // RDP simplification eats inward on corners.
        if (maskDilation > 0)
            solid = DilateMask(solid, w, h, maskDilation);

        // Horizon clamp applied AFTER dilation, so it stays a hard cutoff.
        int yMax = Mathf.RoundToInt(horizonClampV * h);
        for (int i = 0; i < solid.Length; i++)
            if (i / w >= yMax) solid[i] = false;

        List<List<Vector2>> loops = TraceContours(solid, w, h);
        if (loops.Count == 0) return false;

        outers = new List<List<Vector2>>();
        holes = new List<List<Vector2>>();
        foreach (var loop in loops)
        {
            if (Mathf.Abs(SignedArea(loop)) < minRegionArea) continue;
            var simp = SimplifyClosed(loop, simplification);
            if (simp.Count < 3) continue;
            if (SignedArea(simp) > 0f) outers.Add(simp);
            else holes.Add(simp);
        }
        return outers.Count > 0;
    }

    public Mesh GenerateCollisionMesh()
    {
        if (meshSourceMode == MeshSourceMode.Polygon)
            return GeneratePolygonMesh();

        if (maskTexture == null) return null;
        if (warpCamera == null)
        {
            Debug.LogWarning("[Mesh4] Λείπει warp camera — απαιτείται για το camera-unproject.");
            return null;
        }

        return meshGenerationMode == MeshGenerationMode.Grid ? GenerateGridMesh() : GenerateContourMesh();
    }

    /// <summary>
    /// Polygon mode: παίρνει τα Outline Points (LOCAL space), χρησιμοποιεί (x,z) ως 2D
    /// περίγραμμα με Y=floorY (σε local), και τα τριγωνοποιεί με το ίδιο EarClip του
    /// Contour mode. Καμία κάμερα/ray/FOV. Αν doubleSidedPolygon=true, προστίθενται
    /// faces και από τις δύο πλευρές ώστε το AC raycast να το χτυπάει σίγουρα.
    /// </summary>
    Mesh GeneratePolygonMesh()
    {
        if (outlinePoints == null || outlinePoints.Count < 3)
        {
            Debug.LogWarning("[Mesh4] Polygon mode: χρειάζονται τουλάχιστον 3 Outline Points. " +
                             "Πάτα 'Initialize Polygon from Mask' ή πρόσθεσε σημεία.");
            return null;
        }

        var poly2D = new List<Vector2>(outlinePoints.Count);
        foreach (var p in outlinePoints) poly2D.Add(new Vector2(p.x, p.z));

        if (Mathf.Abs(SignedArea(poly2D)) < 1e-6f)
        {
            Debug.LogWarning("[Mesh4] Polygon mode: τα Outline Points έχουν μηδενικό εμβαδό (συγγραμμικά;).");
            return null;
        }

        List<int> tris = EarClip(poly2D);
        if (tris.Count < 3) return null;

        // Local-space vertices: floorY είναι world· μετατροπή σε local Y μέσω inverse.
        // Κρατάμε X/Z από τα points (ήδη local) και βάζουμε σταθερό local-Y που
        // αντιστοιχεί στο world floorY κάτω από αυτό το transform.
        float localFloorY = transform.InverseTransformPoint(new Vector3(0f, floorY, 0f)).y;
        var verts = new List<Vector3>(poly2D.Count);
        foreach (var p in poly2D) verts.Add(new Vector3(p.x, localFloorY, p.y));

        var indices = new List<int>(tris);

        // Top face κοιτάει +Y. Διασφάλισε σωστό winding ώστε το RecalculateNormals
        // να βγάλει normal προς +Y (ώστε το AC raycast από πάνω να το χτυπά).
        if (!FaceIsUpward(verts, indices))
            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                int tmp = indices[i + 1]; indices[i + 1] = indices[i + 2]; indices[i + 2] = tmp;
            }

        if (doubleSidedPolygon)
        {
            // Πρόσθεσε ίδια vertices ξανά με αντίστροφο winding → κάτω face.
            int baseIdx = verts.Count;
            verts.AddRange(verts.GetRange(0, baseIdx));
            int topCount = indices.Count;
            for (int i = 0; i + 2 < topCount; i += 3)
            {
                indices.Add(baseIdx + indices[i]);
                indices.Add(baseIdx + indices[i + 2]);
                indices.Add(baseIdx + indices[i + 1]);
            }
        }

        Mesh mesh = new Mesh { name = gameObject.name + "_NavMesh" };
        if (verts.Count > 65000)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetTriangles(indices, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        statVerts = verts.Count;
        statTris = indices.Count / 3;
        return mesh;
    }

    /// <summary>True αν το πρώτο μη-εκφυλισμένο τρίγωνο έχει normal προς +Y.</summary>
    static bool FaceIsUpward(List<Vector3> verts, List<int> indices)
    {
        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            Vector3 nrm = Vector3.Cross(verts[indices[i + 1]] - verts[indices[i]],
                                        verts[indices[i + 2]] - verts[indices[i]]);
            if (nrm.sqrMagnitude > 1e-10f) return nrm.y > 0f;
        }
        return true;
    }

    /// <summary>
    /// Γεμίζει το outlinePoints με ένα αρχικό περίγραμμα (local space) από τη μάσκα:
    /// trace το μεγαλύτερο outer contour, simplify σε ~initResolution σημεία, και
    /// unproject κάθε σημείο μέσω του τρέχοντος Mask placement (camera ή Linear).
    /// Έτσι ξεκινάς με ένα περίγραμμα που ήδη "κάθεται" εκεί που κάθεται το mask mesh,
    /// και μετά σύρεις τα handles για να το τελειοποιήσεις.
    /// </summary>
    public void InitializePolygonFromMask()
    {
        if (maskTexture == null)
        {
            Debug.LogWarning("[Mesh4] Initialize from Mask: όρισε πρώτα Mask Texture.");
            return;
        }
        if (meshSourceMode == MeshSourceMode.Mask && depthMode == DepthMode.Perspective && warpCamera == null)
        {
            Debug.LogWarning("[Mesh4] Initialize from Mask: το Perspective placement χρειάζεται warpCamera " +
                             "(ή βάλε Depth Mode = Linear).");
            return;
        }

        if (!ComputeWalkablePolygons(out var outers, out _, out int w, out int h))
        {
            Debug.LogWarning("[Mesh4] Initialize from Mask: δεν βρέθηκε βατή περιοχή στη μάσκα.");
            return;
        }

        // Μεγαλύτερο outer.
        List<Vector2> best = null; float bestArea = -1f;
        foreach (var o in outers)
        {
            float a = Mathf.Abs(SignedArea(o));
            if (a > bestArea) { bestArea = a; best = o; }
        }
        if (best == null || best.Count < 3) return;

        // Simplify σε ~initResolution σημεία: αυξάνουμε το epsilon μέχρι να φτάσουμε
        // τον στόχο (binary-ish search, λίγες επαναλήψεις αρκούν).
        List<Vector2> reduced = ReducePointCount(best, Mathf.Max(6, initResolution));

#if UNITY_EDITOR
        Undo.RecordObject(this, "Initialize Polygon From Mask");
#endif
        outlinePoints = new List<Vector3>(reduced.Count);
        foreach (var pt in reduced)
        {
            Vector2 uv = new Vector2(pt.x / w, pt.y / h);
            Vector3 world = UnprojectViewportToWorld(uv);
            outlinePoints.Add(transform.InverseTransformPoint(world));
        }

        // Spawn = κεντροειδές.
        Vector2 c = PolygonCentroid(reduced);
        Vector2 cuv = new Vector2(c.x / w, c.y / h);
        Vector3 cworld = UnprojectViewportToWorld(cuv);
        polygonSpawnPoint = new Vector3(cworld.x, floorY, cworld.z);

        meshSourceMode = MeshSourceMode.Polygon;
#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
#endif
        Debug.Log($"[Mesh4] Initialize from Mask: {outlinePoints.Count} σημεία. " +
                  "Σύρε τα handles στο Scene view για να τα ταιριάξεις στο 3D χώρο.");
        RegenerateLive();
    }

    /// <summary>
    /// Φτιάχνει ένα ορθογώνιο περίγραμμα στο επίπεδο floorY ΧΩΡΙΣ μάσκα: οι 4 γωνίες
    /// προβάλλονται από τις γωνίες του viewport της κάμερας (κάτω-αριστερά → κάτω-δεξιά
    /// → πάνω-δεξιά → πάνω-αριστερά). Το κοντινό άκρο (v=0) πέφτει στο φυσικό βάθος της
    /// κάμερας, το μακρινό (v=1) στο rectFarZ. Με rectSubdivisions>1 προστίθενται
    /// ενδιάμεσα σημεία ανά πλευρά ώστε να μπορείς να 'λυγίσεις' το ορθογώνιο σε σχήμα.
    /// Χρήσιμο όταν η μάσκα είναι εκτός ορίων / άχρηστη — ξεκινάς από ένα καθαρό
    /// ορθογώνιο και το σέρνεις όπου θες.
    /// </summary>
    public void InitializePolygonFromCameraRect()
    {
        if (warpCamera == null)
        {
            Debug.LogWarning("[Mesh4] Initialize from Camera Rect: όρισε warpCamera.");
            return;
        }

        // Γωνίες viewport: v=0 (κοντά) στο φυσικό βάθος, v=1 (μακριά) στο rectFarZ.
        // Παίρνουμε X από το camera-unproject (Perspective floor hit) και βάζουμε
        // χειροκίνητα το Z ώστε το ορθογώνιο να είναι "καθαρό" και ελεγχόμενο.
        Vector3 nearL = RectCorner(0f, 0f, false);
        Vector3 nearR = RectCorner(1f, 0f, false);
        Vector3 farR = RectCorner(1f, 1f, true);
        Vector3 farL = RectCorner(0f, 1f, true);

        int seg = Mathf.Max(1, rectSubdivisions);
        var ring = new List<Vector3>();
        AppendEdge(ring, nearL, nearR, seg); // bottom
        AppendEdge(ring, nearR, farR, seg);  // right
        AppendEdge(ring, farR, farL, seg);   // top
        AppendEdge(ring, farL, nearL, seg);  // left

#if UNITY_EDITOR
        Undo.RecordObject(this, "Initialize Polygon From Camera Rect");
#endif
        outlinePoints = new List<Vector3>(ring.Count);
        foreach (var w in ring)
            outlinePoints.Add(transform.InverseTransformPoint(w));

        Vector3 center = (nearL + nearR + farR + farL) * 0.25f;
        polygonSpawnPoint = new Vector3(center.x, floorY, center.z);

        meshSourceMode = MeshSourceMode.Polygon;
#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
#endif
        Debug.Log($"[Mesh4] Initialize from Camera Rect: {outlinePoints.Count} σημεία " +
                  $"(near→far Z, far={rectFarZ:0.##}). Σύρε τα handles για το τελικό σχήμα.");
        RegenerateLive();
    }

    /// <summary>Γωνία ορθογωνίου στο floorY: X μέσω camera-unproject, Z manual αν far.</summary>
    Vector3 RectCorner(float u, float v, bool far)
    {
        Ray ray = warpCamera.ViewportPointToRay(new Vector3(u, v, 0f));
        Vector3 camPos = warpCamera.transform.position;

        float targetZ;
        if (far)
        {
            targetZ = rectFarZ;
        }
        else
        {
            // Κοντινό άκρο: φυσική τομή με floorY (αν υπάρχει), αλλιώς λίγο μπροστά
            // από την κάμερα.
            Plane floor = new Plane(Vector3.up, new Vector3(0f, floorY, 0f));
            if (floor.Raycast(ray, out float d) && d > 0f)
                targetZ = ray.GetPoint(d).z;
            else
                targetZ = camPos.z + Mathf.Sign(rectFarZ - camPos.z) * 1f;
        }

        float x = ray.origin.x;
        if (Mathf.Abs(ray.direction.z) > 1e-6f)
            x = ray.origin.x + ray.direction.x / ray.direction.z * (targetZ - ray.origin.z);
        return new Vector3(x, floorY, targetZ);
    }

    static void AppendEdge(List<Vector3> ring, Vector3 a, Vector3 b, int segments)
    {
        // Προσθέτει το a και τα ενδιάμεσα, ΧΩΡΙΣ το b (το προσθέτει η επόμενη ακμή).
        for (int i = 0; i < segments; i++)
            ring.Add(Vector3.Lerp(a, b, (float)i / segments));
    }

    /// <summary>Μειώνει ένα κλειστό polygon σε ~target σημεία αυξάνοντας το RDP epsilon.</summary>
    static List<Vector2> ReducePointCount(List<Vector2> poly, int target)
    {
        if (poly.Count <= target) return new List<Vector2>(poly);

        float lo = 0.5f, hi = 0f;
        // bbox diagonal ως άνω όριο epsilon.
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in poly) { minX = Mathf.Min(minX, p.x); minY = Mathf.Min(minY, p.y); maxX = Mathf.Max(maxX, p.x); maxY = Mathf.Max(maxY, p.y); }
        hi = new Vector2(maxX - minX, maxY - minY).magnitude;

        List<Vector2> bestFit = SimplifyClosed(poly, lo);
        for (int iter = 0; iter < 24; iter++)
        {
            float mid = (lo + hi) * 0.5f;
            var s = SimplifyClosed(poly, mid);
            if (s.Count > target) lo = mid;
            else { hi = mid; bestFit = s; }
            if (Mathf.Abs(s.Count - target) <= 1) { bestFit = s; break; }
        }
        return bestFit.Count >= 3 ? bestFit : new List<Vector2>(poly);
    }

    /// <summary>Προσθέτει νέο σημείο ανάμεσα στο τελευταίο και το πρώτο (κλειστό loop).</summary>
    public void AddOutlinePointAfterLast()
    {
        if (outlinePoints == null) outlinePoints = new List<Vector3>();
#if UNITY_EDITOR
        Undo.RecordObject(this, "Add Outline Point");
#endif
        Vector3 np;
        if (outlinePoints.Count >= 2)
            np = Vector3.Lerp(outlinePoints[outlinePoints.Count - 1], outlinePoints[0], 0.5f);
        else if (outlinePoints.Count == 1)
            np = outlinePoints[0] + Vector3.right;
        else
            np = transform.InverseTransformPoint(new Vector3(0f, floorY, 0f));
        outlinePoints.Add(np);
#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
#endif
        RegenerateLive();
    }

    public void RemoveLastOutlinePoint()
    {
        if (outlinePoints == null || outlinePoints.Count == 0) return;
#if UNITY_EDITOR
        Undo.RecordObject(this, "Remove Outline Point");
#endif
        outlinePoints.RemoveAt(outlinePoints.Count - 1);
#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
#endif
        RegenerateLive();
    }

    /// <summary>
    /// Grid mode — "Script 1" style: ένα quad ανά (downsampled) pixel της μάσκας. Κάθε
    /// γωνία προβάλλεται με το UnprojectViewportToWorld του Mesh4, που πλέον ΔΕΝ
    /// αποτυγχάνει ποτέ (depth-clamp αντί για radial-clamp) — άρα κανένα quad δεν
    /// απορρίπτεται κοντά στον ορίζοντα, και η κάλυψη ταιριάζει 1:1 με τη μάσκα
    /// (μείον το downsampling). Πιο πολλά πολύγωνα από το Contour mode.
    /// </summary>
    Mesh GenerateGridMesh()
    {
        Color32[] pixels = ReadPixels(maskTexture, out int texW, out int texH);
        if (pixels == null || pixels.Length != texW * texH) return null;

        bool[] solid = new bool[texW * texH];
        int thr = Mathf.RoundToInt(threshold * 255f);
        for (int i = 0; i < solid.Length; i++)
        {
            Color32 c = pixels[i];
            solid[i] = (c.r + c.g + c.b) / 3 > thr;
        }
        if (maskDilation > 0) solid = DilateMask(solid, texW, texH, maskDilation);

        int yMax = Mathf.RoundToInt(horizonClampV * texH);
        for (int i = 0; i < solid.Length; i++)
            if (i / texW >= yMax) solid[i] = false;

        int ds = Mathf.Max(1, downsampling);
        int gw = Mathf.Max(1, texW / ds);
        int gh = Mathf.Max(1, texH / ds);

        // Walkable map ανά grid cell.
        var cell = new bool[gw * gh];
        for (int gy = 0; gy < gh; gy++)
        {
            int py0 = gy * ds, py1 = Mathf.Min(py0 + ds, texH);
            for (int gx = 0; gx < gw; gx++)
            {
                int px0 = gx * ds, px1 = Mathf.Min(px0 + ds, texW);
                bool walkable = false;
                for (int py = py0; py < py1 && !walkable; py++)
                    for (int px = px0; px < px1; px++)
                        if (solid[py * texW + px]) { walkable = true; break; }
                cell[gy * gw + gx] = walkable;
            }
        }

        var vertsWorld = new List<Vector3>();
        var indices = new List<int>();

        // Simplification στο Grid mode = greedy horizontal RUN merging: αντί για ένα
        // quad ανά cell, ενώνει συνεχόμενα walkable cells της ίδιας γραμμής σε ΕΝΑ
        // φαρδύ quad. Το simplification ορίζει το μέγιστο μήκος run (σε cells):
        // 0 = κανένα merge (ένα quad/cell, όπως πριν)· μεγαλύτερο = λιγότερα, φαρδύτερα
        // quads. Η κάλυψη παραμένει ίδια — απλά λιγότερα τρίγωνα.
        int maxRun = Mathf.Max(1, Mathf.RoundToInt(simplification));
        if (maxRun <= 1) maxRun = 1;

        for (int gy = 0; gy < gh; gy++)
        {
            float v0 = (float)gy / gh, v1 = (float)(gy + 1) / gh;
            int gx = 0;
            while (gx < gw)
            {
                if (!cell[gy * gw + gx]) { gx++; continue; }
                int runStart = gx;
                int runLen = 0;
                while (gx < gw && cell[gy * gw + gx] && runLen < maxRun) { gx++; runLen++; }

                float u0 = (float)runStart / gw, u1 = (float)(runStart + runLen) / gw;

                int vi = vertsWorld.Count;
                vertsWorld.Add(UnprojectViewportToWorld(new Vector2(u0, v0)));
                vertsWorld.Add(UnprojectViewportToWorld(new Vector2(u1, v0)));
                vertsWorld.Add(UnprojectViewportToWorld(new Vector2(u0, v1)));
                vertsWorld.Add(UnprojectViewportToWorld(new Vector2(u1, v1)));

                indices.Add(vi); indices.Add(vi + 2); indices.Add(vi + 1);
                indices.Add(vi + 1); indices.Add(vi + 2); indices.Add(vi + 3);
            }
        }

        return FinalizeMesh(vertsWorld, indices);
    }

    /// <summary>
    /// Contour mode (από v3): contour trace + simplify + ear-clip. Πολύ λιγότερα πολύγωνα
    /// από το Grid mode, αλλά το Simplification "τρώει" ελαφρά τις γωνίες (Mask Dilation
    /// το αντισταθμίζει).
    /// </summary>
    Mesh GenerateContourMesh()
    {
        if (!ComputeWalkablePolygons(out var outers, out var holes, out int w, out int h)) return null;

        var holeGroups = new List<List<Vector2>>[outers.Count];
        for (int i = 0; i < outers.Count; i++) holeGroups[i] = new List<List<Vector2>>();
        foreach (var hole in holes)
            for (int i = 0; i < outers.Count; i++)
                if (PointInPolygon(hole[0], outers[i])) { holeGroups[i].Add(hole); break; }

        var verts2D = new List<Vector2>();
        var indices = new List<int>();
        for (int i = 0; i < outers.Count; i++)
        {
            List<Vector2> poly = MergeHoles(outers[i], holeGroups[i]);
            int baseIndex = verts2D.Count;
            List<int> tris = EarClip(poly);
            verts2D.AddRange(poly);
            for (int k = 0; k < tris.Count; k++) indices.Add(baseIndex + tris[k]);
        }
        if (indices.Count < 3) return null;

        // mask-px ➜ UV ➜ world floor (camera unproject)
        var vertsWorld = new List<Vector3>(verts2D.Count);
        for (int i = 0; i < verts2D.Count; i++)
        {
            Vector2 uv = new Vector2(verts2D[i].x / w, verts2D[i].y / h);
            vertsWorld.Add(UnprojectViewportToWorld(uv));
        }

        return FinalizeMesh(vertsWorld, indices);
    }

    /// <summary>
    /// Κοινό τελικό βήμα: log βάθους, orient normals προς την κάμερα, world➜local,
    /// build του Unity Mesh. Χρησιμοποιείται και από τα δύο generation modes.
    /// </summary>
    Mesh FinalizeMesh(List<Vector3> vertsWorld, List<int> indices)
    {
        if (indices.Count < 3 || vertsWorld.Count == 0) return null;

        if (logDepthRange)
        {
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var p in vertsWorld) { minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z); }
            Debug.Log($"[Mesh4] Z range: {minZ:F2} → {maxZ:F2} (Δ {maxZ - minZ:F2}). Verts: {vertsWorld.Count}, Tris: {indices.Count / 3}.");
        }

        // Normals προς την κάμερα (Mask mode) ή προς τα πάνω (Polygon mode, χωρίς
        // κάμερα), ώστε το AC raycast να χτυπάει το μπροστινό face.
        Vector3 refDir = (warpCamera != null) ? warpCamera.transform.forward : Vector3.down;
        bool flip = false;
        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            Vector3 nrm = Vector3.Cross(vertsWorld[indices[i + 1]] - vertsWorld[indices[i]],
                                        vertsWorld[indices[i + 2]] - vertsWorld[indices[i]]);
            if (nrm.sqrMagnitude > 1e-10f) { flip = Vector3.Dot(nrm, refDir) > 0f; break; }
        }
        if (flip)
            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                int tmp = indices[i + 1]; indices[i + 1] = indices[i + 2]; indices[i + 2] = tmp;
            }

        var vertsLocal = new Vector3[vertsWorld.Count];
        for (int i = 0; i < vertsWorld.Count; i++)
            vertsLocal[i] = transform.InverseTransformPoint(vertsWorld[i]);

        Mesh mesh = new Mesh { name = gameObject.name + "_NavMesh" };
        if (vertsLocal.Length > 65000)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = vertsLocal;
        mesh.triangles = indices.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // -------------------------------------------------------------- Pixels

    static Color32[] ReadPixels(Texture2D tex, out int w, out int h)
    {
        w = tex.width; h = tex.height;
        try { if (tex.isReadable) return tex.GetPixels32(); } catch { }
        RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(tex, rt);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D tmp = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tmp.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        Color32[] px = tmp.GetPixels32();
        DestroySafe(tmp);
        return px;
    }

    // -------------------------------------------------------------- Dilation

    /// <summary>
    /// Separable square dilation (OR over a (2*radius+1)x(2*radius+1) window),
    /// O(w*h) via sliding-window counts. Grows the walkable area outward by
    /// 'radius' pixels and shrinks small non-walkable holes by the same amount.
    /// </summary>
    static bool[] DilateMask(bool[] src, int w, int h, int radius)
    {
        if (radius <= 0) return src;

        var tmp = new bool[src.Length];
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            int count = 0;
            for (int i = 0; i <= radius && i < w; i++)
                if (src[rowBase + i]) count++;

            for (int x = 0; x < w; x++)
            {
                tmp[rowBase + x] = count > 0;
                int addIdx = x + 1 + radius;
                int remIdx = x - radius;
                if (addIdx < w && src[rowBase + addIdx]) count++;
                if (remIdx >= 0 && src[rowBase + remIdx]) count--;
            }
        }

        var dst = new bool[src.Length];
        for (int x = 0; x < w; x++)
        {
            int count = 0;
            for (int i = 0; i <= radius && i < h; i++)
                if (tmp[i * w + x]) count++;

            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = count > 0;
                int addIdx = y + 1 + radius;
                int remIdx = y - radius;
                if (addIdx < h && tmp[addIdx * w + x]) count++;
                if (remIdx >= 0 && tmp[remIdx * w + x]) count--;
            }
        }
        return dst;
    }

    // ------------------------------------------------------------- Contours

    static List<List<Vector2>> TraceContours(bool[] solid, int w, int h)
    {
        bool S(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && solid[y * w + x];
        int stride = w + 1;
        int P(int x, int y) => y * stride + x;
        Vector2 ToV(int p) => new Vector2(p % stride, p / stride);

        var edges = new Dictionary<int, List<int>>();
        void AddEdge(int a, int b)
        {
            if (!edges.TryGetValue(a, out var list)) { list = new List<int>(2); edges[a] = list; }
            list.Add(b);
        }

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!S(x, y)) continue;
                if (!S(x, y - 1)) AddEdge(P(x, y), P(x + 1, y));
                if (!S(x + 1, y)) AddEdge(P(x + 1, y), P(x + 1, y + 1));
                if (!S(x, y + 1)) AddEdge(P(x + 1, y + 1), P(x, y + 1));
                if (!S(x - 1, y)) AddEdge(P(x, y + 1), P(x, y));
            }

        var loops = new List<List<Vector2>>();
        while (edges.Count > 0)
        {
            int start = -1;
            foreach (var kv in edges) { start = kv.Key; break; }
            var loopPts = new List<int>();
            int current = start;
            float prevDX = 0f, prevDY = 0f;
            int guard = (w + 1) * (h + 1) * 4;
            bool closed = false;

            while (guard-- > 0)
            {
                if (!edges.TryGetValue(current, out var outs) || outs.Count == 0) break;
                int chosen = 0;
                if (outs.Count > 1 && loopPts.Count > 0)
                {
                    float best = float.NegativeInfinity;
                    Vector2 c = ToV(current);
                    for (int i = 0; i < outs.Count; i++)
                    {
                        Vector2 nxt = ToV(outs[i]);
                        float cross = prevDX * (nxt.y - c.y) - prevDY * (nxt.x - c.x);
                        if (cross > best) { best = cross; chosen = i; }
                    }
                }
                int next = outs[chosen];
                outs.RemoveAt(chosen);
                if (outs.Count == 0) edges.Remove(current);
                loopPts.Add(current);
                Vector2 cv = ToV(current), nv = ToV(next);
                prevDX = nv.x - cv.x; prevDY = nv.y - cv.y;
                current = next;
                if (current == start) { closed = true; break; }
            }

            if (closed && loopPts.Count >= 3)
            {
                var pts = new List<Vector2>(loopPts.Count);
                foreach (int p in loopPts) pts.Add(ToV(p));
                pts = RemoveCollinear(pts);
                if (pts.Count >= 3) loops.Add(pts);
            }
        }
        return loops;
    }

    static List<Vector2> RemoveCollinear(List<Vector2> pts)
    {
        int n = pts.Count;
        var res = new List<Vector2>(n);
        for (int i = 0; i < n; i++)
        {
            Vector2 prev = pts[(i - 1 + n) % n], cur = pts[i], next = pts[(i + 1) % n];
            float cross = (cur.x - prev.x) * (next.y - cur.y) - (cur.y - prev.y) * (next.x - cur.x);
            if (Mathf.Abs(cross) > 1e-6f) res.Add(cur);
        }
        return res;
    }

    static List<Vector2> SimplifyClosed(List<Vector2> pts, float eps)
    {
        if (eps <= 0.001f || pts.Count < 5) return new List<Vector2>(pts);
        int far = 0; float best = -1f;
        for (int i = 1; i < pts.Count; i++)
        {
            float d = (pts[i] - pts[0]).sqrMagnitude;
            if (d > best) { best = d; far = i; }
        }
        var a = new List<Vector2>(); var b = new List<Vector2>();
        for (int i = 0; i <= far; i++) a.Add(pts[i]);
        for (int i = far; i < pts.Count; i++) b.Add(pts[i]);
        b.Add(pts[0]);
        var sa = RDP(a, eps); var sb = RDP(b, eps);
        var res = new List<Vector2>(sa);
        res.RemoveAt(res.Count - 1);
        res.AddRange(sb);
        res.RemoveAt(res.Count - 1);
        return res;
    }

    static List<Vector2> RDP(List<Vector2> pts, float eps)
    {
        if (pts.Count < 3) return new List<Vector2>(pts);
        var keep = new bool[pts.Count];
        keep[0] = keep[pts.Count - 1] = true;
        var stack = new Stack<(int s, int e)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            float maxD = -1f; int idx = -1;
            Vector2 A = pts[s], B = pts[e];
            for (int i = s + 1; i < e; i++)
            {
                float d = PointSegmentDistance(pts[i], A, B);
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (idx != -1 && maxD > eps) { keep[idx] = true; stack.Push((s, idx)); stack.Push((idx, e)); }
        }
        var res = new List<Vector2>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) res.Add(pts[i]);
        return res;
    }

    static float PointSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-10f) return (p - a).magnitude;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return (p - (a + ab * t)).magnitude;
    }

    // -------------------------------------------------------------- Geometry

    static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    static float SignedArea(List<Vector2> p)
    {
        float a = 0f;
        for (int i = 0; i < p.Count; i++)
        {
            Vector2 c = p[i], n = p[(i + 1) % p.Count];
            a += c.x * n.y - n.x * c.y;
        }
        return a * 0.5f;
    }

    static bool PointInPolygon(Vector2 p, List<Vector2> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            Vector2 a = poly[i], b = poly[j];
            if ((a.y > p.y) != (b.y > p.y) &&
                p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                inside = !inside;
        }
        return inside;
    }

    static bool IsReflex(List<Vector2> poly, int i)
    {
        Vector2 a = poly[(i - 1 + poly.Count) % poly.Count];
        Vector2 b = poly[i];
        Vector2 c = poly[(i + 1) % poly.Count];
        return Cross(b - a, c - b) < 0f;
    }

    static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
        bool hasNeg = (d1 < 0f) || (d2 < 0f) || (d3 < 0f);
        bool hasPos = (d1 > 0f) || (d2 > 0f) || (d3 > 0f);
        return !(hasNeg && hasPos);
    }

    static bool PointInTriangleStrict(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        return Cross(b - a, p - a) > 1e-9f && Cross(c - b, p - b) > 1e-9f && Cross(a - c, p - c) > 1e-9f;
    }

    static List<Vector2> MergeHoles(List<Vector2> outer, List<List<Vector2>> holes)
    {
        var poly = new List<Vector2>(outer);
        if (holes == null || holes.Count == 0) return poly;
        float MaxX(List<Vector2> hh) { float m = float.NegativeInfinity; foreach (var v in hh) if (v.x > m) m = v.x; return m; }
        holes.Sort((h1, h2) => MaxX(h2).CompareTo(MaxX(h1)));
        foreach (var hole in holes) poly = MergeOneHole(poly, hole);
        return poly;
    }

    static List<Vector2> MergeOneHole(List<Vector2> poly, List<Vector2> hole)
    {
        int mi = 0;
        for (int i = 1; i < hole.Count; i++) if (hole[i].x > hole[mi].x) mi = i;
        Vector2 M = hole[mi];
        float bestX = float.PositiveInfinity; int bestEdge = -1; Vector2 I = M;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 a = poly[i], b = poly[(i + 1) % poly.Count];
            if ((a.y > M.y) == (b.y > M.y)) continue;
            float x = a.x + (M.y - a.y) * (b.x - a.x) / (b.y - a.y);
            if (x >= M.x - 1e-5f && x < bestX) { bestX = x; bestEdge = i; I = new Vector2(x, M.y); }
        }
        if (bestEdge < 0) bestEdge = 0;
        Vector2 ea = poly[bestEdge], eb = poly[(bestEdge + 1) % poly.Count];
        int candIdx = ea.x > eb.x ? bestEdge : (bestEdge + 1) % poly.Count;
        Vector2 Pp = poly[candIdx];
        int chosen = candIdx; float bestMetric = float.PositiveInfinity; bool blocked = false;
        for (int i = 0; i < poly.Count; i++)
        {
            if (i == candIdx) continue;
            Vector2 v = poly[i];
            if (!IsReflex(poly, i)) continue;
            if (!PointInTriangle(v, M, I, Pp)) continue;
            Vector2 d = v - M;
            float angle = Mathf.Abs(Mathf.Atan2(d.y, d.x));
            float metric = angle * 1000f + d.sqrMagnitude * 1e-6f;
            if (!blocked || metric < bestMetric) { bestMetric = metric; chosen = i; blocked = true; }
        }
        var result = new List<Vector2>(poly.Count + hole.Count + 2);
        for (int i = 0; i <= chosen; i++) result.Add(poly[i]);
        for (int k = 0; k <= hole.Count; k++) result.Add(hole[(mi + k) % hole.Count]);
        result.Add(poly[chosen]);
        for (int i = chosen + 1; i < poly.Count; i++) result.Add(poly[i]);
        return result;
    }

    /// <summary>
    /// Ποιότητα ear = min/max edge length ratio του τριγώνου (a,b,c). 1 = ισόπλευρο
    /// (καλό), ~0 = πολύ λεπτό "ακτίνα" τρίγωνο (κακό). Χρησιμοποιείται για να
    /// προτιμηθούν "τετράγωνα" τρίγωνα (zigzag strip) αντί για fan από ένα σημείο.
    /// </summary>
    static float EarQuality(Vector2 a, Vector2 b, Vector2 c)
    {
        float ab = (b - a).magnitude;
        float bc = (c - b).magnitude;
        float ca = (a - c).magnitude;
        float maxEdge = Mathf.Max(ab, Mathf.Max(bc, ca));
        if (maxEdge < 1e-9f) return 0f;
        float minEdge = Mathf.Min(ab, Mathf.Min(bc, ca));
        return minEdge / maxEdge;
    }

    static List<int> EarClip(List<Vector2> poly)
    {
        var tris = new List<int>();
        int n = poly.Count;
        if (n < 3) return tris;
        var idx = new List<int>(n);
        if (SignedArea(poly) < 0f) for (int i = n - 1; i >= 0; i--) idx.Add(i);
        else for (int i = 0; i < n; i++) idx.Add(i);

        // Για μικρά/μεσαία πολύγωνα (μετά το Simplification), σε κάθε βήμα
        // διαλέγουμε το ear με την καλύτερη ποιότητα (πιο "τετράγωνο" τρίγωνο)
        // αντί για το πρώτο έγκυρο — αυτό δίνει zigzag-strip triangulation σε
        // λεπτά μονοπάτια αντί για ακτίνες/fan από ένα σημείο. Για πολύ μεγάλα
        // πολύγωνα γυρίζουμε στο γρηγορότερο "πρώτο έγκυρο ear" ώστε το
        // auto-regen να μη γίνεται αργό όσο κινείς την κάμερα.
        bool bestEarMode = n <= 220;

        long guard = (long)n * n + 100;
        while (idx.Count > 3 && guard-- > 0)
        {
            int bestI = -1, bI0 = 0, bI1 = 0, bI2 = 0;
            float bestQ = -1f;

            for (int i = 0; i < idx.Count; i++)
            {
                int i0 = idx[(i - 1 + idx.Count) % idx.Count];
                int i1 = idx[i];
                int i2 = idx[(i + 1) % idx.Count];
                Vector2 a = poly[i0], b = poly[i1], c = poly[i2];
                if (Cross(b - a, c - b) <= 1e-9f) continue;

                bool ear = true;
                for (int j = 0; j < idx.Count; j++)
                {
                    int vj = idx[j];
                    if (vj == i0 || vj == i1 || vj == i2) continue;
                    Vector2 p = poly[vj];
                    if (p == a || p == b || p == c) continue;
                    if (PointInTriangleStrict(p, a, b, c)) { ear = false; break; }
                }
                if (!ear) continue;

                if (!bestEarMode) { bestI = i; bI0 = i0; bI1 = i1; bI2 = i2; break; }

                float q = EarQuality(a, b, c);
                if (q > bestQ) { bestQ = q; bestI = i; bI0 = i0; bI1 = i1; bI2 = i2; }
            }

            if (bestI != -1)
            {
                tris.Add(bI0); tris.Add(bI1); tris.Add(bI2);
                idx.RemoveAt(bestI);
                continue;
            }

            // Fallback (κανένα έγκυρο ear λόγω αριθμητικού θορύβου): κόψε στη
            // πιο "οξεία" κυρτή γωνία που βρίσκεις, ό,τι κι αν προκαλέσει.
            int besti = -1; float bestA = float.PositiveInfinity;
            for (int i = 0; i < idx.Count; i++)
            {
                int i0 = idx[(i - 1 + idx.Count) % idx.Count];
                int i1 = idx[i];
                int i2 = idx[(i + 1) % idx.Count];
                float cr = Cross(poly[i1] - poly[i0], poly[i2] - poly[i1]);
                if (cr > 0f && cr < bestA) { bestA = cr; besti = i; }
            }
            if (besti < 0) besti = 0;
            int a0 = idx[(besti - 1 + idx.Count) % idx.Count];
            int a1 = idx[besti];
            int a2 = idx[(besti + 1) % idx.Count];
            tris.Add(a0); tris.Add(a1); tris.Add(a2);
            idx.RemoveAt(besti);
        }
        if (idx.Count == 3) { tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]); }
        return tris;
    }

    // -------------------------------------------------------------- Bake

#if UNITY_EDITOR
    public string GetDefaultBakePath()
    {
        string sceneName = (gameObject.scene.IsValid() && !string.IsNullOrEmpty(gameObject.scene.name))
            ? gameObject.scene.name : "Scene";
        return "Assets/NavMeshBakes/" + sceneName + "_" + gameObject.name + "_NavMesh.asset";
    }

    public void BakeToAsset()
    {
        Mesh fresh = GenerateCollisionMesh();
        if (fresh == null) { Debug.LogWarning("[Mesh4] Bake απέτυχε: δεν παράχθηκε mesh."); return; }
        if (!AssetDatabase.IsValidFolder("Assets/NavMeshBakes"))
            AssetDatabase.CreateFolder("Assets", "NavMeshBakes");
        string path = GetDefaultBakePath();
        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null)
        {
            existing.Clear();
            existing.indexFormat = fresh.indexFormat;
            existing.vertices = fresh.vertices;
            existing.triangles = fresh.triangles;
            existing.normals = fresh.normals;
            existing.RecalculateBounds();
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            bakedMeshAsset = existing;
            DestroySafe(fresh);
        }
        else
        {
            AssetDatabase.CreateAsset(fresh, path);
            AssetDatabase.SaveAssets();
            bakedMeshAsset = fresh;
        }
        AssignMesh(bakedMeshAsset);
        lastHash = ComputeHash();          // αποφεύγει άμεση αυτο-ακύρωση του bake που μόλις φτιάχτηκε
        EditorUtility.SetDirty(this);
        Debug.Log("[Mesh4] Bake OK: " + path);
    }

    public void ClearBakedAsset(bool silent = false)
    {
        if (bakedMeshAsset != null)
        {
            string p = AssetDatabase.GetAssetPath(bakedMeshAsset);
            if (!string.IsNullOrEmpty(p)) AssetDatabase.DeleteAsset(p);
        }
        bakedMeshAsset = null;
        EditorUtility.SetDirty(this);
        Debug.Log(silent
            ? "[Mesh4] Παράμετρος άλλαξε — το bake διαγράφηκε αυτόματα, live mode."
            : "[Mesh4] Bake διαγράφηκε — live mode.");
    }
#endif

    // -------------------------------------------------------- AC Integration

#if UNITY_EDITOR
    /// <summary>
    /// Προσθέτει/ρυθμίζει AC.NavigationMesh (Ignore Collisions = off) και
    /// AC.ConstantID (για αναφορές σε saves) σε αυτό το GameObject.
    /// Δουλεύει μέσω reflection, ώστε να μη χρειάζεται hard reference στο AC —
    /// αν το Adventure Creator δεν υπάρχει στο project, απλά προειδοποιεί.
    /// </summary>
    public void SetupACComponents()
    {
        // ── AC.NavigationMesh (Mesh Collider pathfinding mode) ──
        var navType = FindACType("NavigationMesh");
        if (navType != null)
        {
            var nav = GetComponent(navType);
            if (nav == null) nav = Undo.AddComponent(gameObject, navType);

            var so = new SerializedObject(nav);
            var ignoreCollisions = so.FindProperty("ignoreCollisions");
            if (ignoreCollisions != null)
            {
                ignoreCollisions.boolValue = false;
                so.ApplyModifiedProperties();
            }
            else
            {
                LogMissingBoolField(so, "AC.NavigationMesh", "ignoreCollisions");
            }
            EditorUtility.SetDirty(nav);
        }
        else
        {
            Debug.LogWarning("[Mesh4] Δεν βρέθηκε ο τύπος AC.NavigationMesh — λείπει το Adventure Creator από το project;");
        }

        // ── AC.ConstantID (για να αναφέρεται το αντικείμενο σε saves) ──
        var cidType = FindACType("ConstantID");
        if (cidType != null)
        {
            var cid = GetComponent(cidType);
            if (cid == null) cid = Undo.AddComponent(gameObject, cidType);

            var so = new SerializedObject(cid);
            var idProp = so.FindProperty("constantID");
            if (idProp != null && idProp.intValue == 0)
            {
                idProp.intValue = UnityEngine.Random.Range(int.MinValue + 1, int.MaxValue);
                so.ApplyModifiedProperties();
            }
            EditorUtility.SetDirty(cid);
        }
        else
        {
            Debug.LogWarning("[Mesh4] Δεν βρέθηκε ο τύπος AC.ConstantID — λείπει το Adventure Creator από το project;");
        }

        EditorUtility.SetDirty(this);
    }

    static System.Type FindACType(string name)
    {
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType("AC." + name) ?? asm.GetType(name);
            if (t != null) return t;
        }
        return null;
    }

    static void LogMissingBoolField(SerializedObject so, string componentName, string wanted)
    {
        var names = new List<string>();
        var prop = so.GetIterator();
        bool enterChildren = true;
        while (prop.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (prop.propertyType == SerializedPropertyType.Boolean) names.Add(prop.name);
        }
        Debug.LogWarning($"[Mesh4] Το πεδίο '{wanted}' δεν βρέθηκε στο {componentName} (μπορεί να άλλαξε όνομα σε άλλη έκδοση AC). " +
                         $"Διαθέσιμα bool πεδία: {string.Join(", ", names)}. Όρισέ το χειροκίνητα στο Inspector.");
    }
#endif

    // ---------------------------------------------------------------- Gizmos

    void OnDrawGizmos()
    {
        if (gizmoMode == GizmoMode.Always) DrawNavGizmos();
#if UNITY_EDITOR
        else if (gizmoMode == GizmoMode.WhenCamera && warpCamera != null && Selection.Contains(warpCamera.gameObject))
            DrawNavGizmos();
#endif
    }

    void OnDrawGizmosSelected()
    {
        if (gizmoMode == GizmoMode.WhenSelected || gizmoMode == GizmoMode.WhenCamera)
            DrawNavGizmos();
    }

    void DrawNavGizmos()
    {
        Mesh m = GetActiveMesh();
        if (m != null)
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = fillColor; Gizmos.DrawMesh(m);
            Gizmos.color = wireColor; Gizmos.DrawWireMesh(m);
            Gizmos.matrix = Matrix4x4.identity;
        }

        Vector3 sp = GetSpawnWorldPosition();
        float gizSize;
        if (meshSourceMode == MeshSourceMode.Polygon)
        {
            Bounds b = new Bounds(sp, Vector3.zero);
            if (outlinePoints != null)
                foreach (var p in outlinePoints)
                    b.Encapsulate(transform.TransformPoint(p));
            gizSize = Mathf.Max(0.05f, b.size.magnitude * 0.02f);
        }
        else
        {
            if (warpCamera == null) return;
            Vector3 widthRef = UnprojectViewportToWorld(new Vector2(1f, 0f)) - UnprojectViewportToWorld(new Vector2(0f, 0f));
            gizSize = Mathf.Max(0.05f, widthRef.magnitude * 0.02f);
        }

        Gizmos.color = Color.yellow;
        Gizmos.DrawSphere(sp, gizSize);
        Gizmos.DrawLine(sp, sp + Vector3.up * gizSize * 5f);

        if (showDepthLimitGizmo && meshSourceMode == MeshSourceMode.Mask && depthMode == DepthMode.Perspective && warpCamera != null)
            DrawDepthLimitGizmo();
    }

    /// <summary>
    /// Δύο πορτοκαλί πλαίσια στο Z = camera.z ± Max Floor Distance. Αν το "πάνω"
    /// (μακρινό) τμήμα του mesh σου φαίνεται μπροστά από κάποιο hotspot, σύγκρινε
    /// τη θέση τους με αυτά τα πλαίσια — αν το hotspot είναι ΠΙΣΩ από το πλαίσιο,
    /// το mesh εκεί κόβεται από το Max Floor Distance· μεγάλωσέ το.
    /// </summary>
    void DrawDepthLimitGizmo()
    {
        Vector3 camPos = warpCamera.transform.position;
        float size = Mathf.Max(maxFloorDistance, 1f);
        Color c = new Color(1f, 0.45f, 0f, 0.9f);

        for (int s = -1; s <= 1; s += 2)
        {
            float z = camPos.z + s * maxFloorDistance;
            Vector3 a = new Vector3(camPos.x - size, floorY, z);
            Vector3 b = new Vector3(camPos.x + size, floorY, z);
            Vector3 ta = new Vector3(camPos.x - size, floorY + size * 0.5f, z);
            Vector3 tb = new Vector3(camPos.x + size, floorY + size * 0.5f, z);

            Gizmos.color = c;
            Gizmos.DrawLine(a, b);
            Gizmos.DrawLine(a, ta);
            Gizmos.DrawLine(b, tb);
            Gizmos.DrawLine(ta, tb);

#if UNITY_EDITOR
            Handles.color = c;
            Handles.Label((a + tb) * 0.5f, $"Max Floor Distance limit (Z={z:0.#})");
#endif
        }
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(Mesh4))]
public class Mesh4Editor : Editor
{
    public override void OnInspectorGUI()
    {
        var m4 = (Mesh4)target;
        serializedObject.Update();

        // Walk όλες τις serialized properties με τη σειρά· μετά από συγκεκριμένα
        // πεδία εισάγουμε inline το αντίστοιχο κουμπί.
        SerializedProperty prop = serializedObject.GetIterator();
        bool enter = true;
        while (prop.NextVisible(enter))
        {
            enter = false;
            if (prop.name == "m_Script") continue;

            EditorGUILayout.PropertyField(prop, true);

            switch (prop.name)
            {
                // ── Polygon: κουμπιά δίπλα στο outline / init πεδία ──
                case "initResolution":
                    if (m4.meshSourceMode == Mesh4.MeshSourceMode.Polygon)
                        InlineButton($"⟳  Initialize Polygon from Mask ({m4.initResolution} σημεία)",
                            () => m4.InitializePolygonFromMask());
                    break;

                case "rectSubdivisions":
                    if (m4.meshSourceMode == Mesh4.MeshSourceMode.Polygon)
                        InlineButton($"▭  Initialize Polygon from Camera Rect (far Z = {m4.rectFarZ:0.##})",
                            () => m4.InitializePolygonFromCameraRect());
                    break;

                case "outlinePoints":
                    if (m4.meshSourceMode == Mesh4.MeshSourceMode.Polygon)
                    {
                        EditorGUILayout.BeginHorizontal();
                        if (GUILayout.Button("＋ Σημείο")) { m4.AddOutlinePointAfterLast(); SceneView.RepaintAll(); }
                        using (new EditorGUI.DisabledScope(m4.outlinePoints == null || m4.outlinePoints.Count == 0))
                            if (GUILayout.Button("－ Τελευταίο")) { m4.RemoveLastOutlinePoint(); SceneView.RepaintAll(); }
                        EditorGUILayout.EndHorizontal();
                    }
                    break;

                // ── Camera tilt helper δίπλα στο warpCamera ──
                case "warpCamera":
                    DrawHorizonTiltHelper(m4);
                    break;

                // ── Surface snap δίπλα στο targetSurfaceZ ──
                case "targetSurfaceZ":
                    InlineButton($"Snap Surface to Z = {m4.targetSurfaceZ:0.##}", () => m4.SnapSurfaceToZ());
                    break;

                // ── Baked asset: bake / clear δίπλα στο πεδίο ──
                case "bakedMeshAsset":
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button(m4.bakedMeshAsset != null ? "Re-Bake" : "Bake σε Asset"))
                        m4.BakeToAsset();
                    using (new EditorGUI.DisabledScope(m4.bakedMeshAsset == null))
                        if (GUILayout.Button("Διαγραφή Bake")) m4.ClearBakedAsset();
                    EditorGUILayout.EndHorizontal();
                    break;

                // ── Spawn: κουμπιά δίπλα στο spawn πεδίο του εκάστοτε mode ──
                case "playerSpawnUV":
                    if (m4.meshSourceMode == Mesh4.MeshSourceMode.Mask) DrawSpawnButtons(m4);
                    break;
                case "polygonSpawnPoint":
                    if (m4.meshSourceMode == Mesh4.MeshSourceMode.Polygon) DrawSpawnButtons(m4);
                    break;
            }
        }

        serializedObject.ApplyModifiedProperties();

        EditorGUILayout.Space();

        // ── Γενικά (όχι δεμένα σε ένα πεδίο) ──
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Regenerate", GUILayout.Height(26)))
        {
            if (m4.bakedMeshAsset != null && m4.autoReBake) m4.BakeToAsset();
            else
            {
                if (m4.bakedMeshAsset != null) m4.ClearBakedAsset(true);
                m4.RegenerateLive();
            }
            SceneView.RepaintAll();
        }
        if (GUILayout.Button("AC Setup (NavigationMesh + ConstantID)", GUILayout.Height(26)))
            m4.SetupACComponents();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        DrawInfoBox(m4);
    }

    void InlineButton(string label, System.Action action)
    {
        if (GUILayout.Button(label))
        {
            action();
            SceneView.RepaintAll();
        }
    }

    void DrawSpawnButtons(Mesh4 m4)
    {
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("🎯 Spawn ➜ Κέντρο")) { m4.CenterSpawnOnMesh(); SceneView.RepaintAll(); }
        if (GUILayout.Button("▶ Τοποθέτησε Player")) m4.PlacePlayerAtSpawn();
        EditorGUILayout.EndHorizontal();
    }

    void DrawHorizonTiltHelper(Mesh4 m4)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PrefixLabel(new GUIContent("Horizon v (ζωγραφιά)",
                "Πού βλέπεις τον ορίζοντα/eye-level στη ζωγραφιά (0=κάτω, 1=πάνω). " +
                "Το κουμπί υπολογίζει το rotation X της κάμερας ώστε ο ορίζοντάς της να πέσει εκεί."));
            m4.horizonV = EditorGUILayout.Slider(m4.horizonV, 0.05f, 0.95f);
        }
        if (GUILayout.Button($"⤢  Set Camera Tilt από Horizon v (= {m4.SuggestedTiltX():0.#}°)"))
            m4.ApplyHorizonTilt();
    }

    void DrawInfoBox(Mesh4 m4)
    {
        string bakeInfo = m4.bakedMeshAsset != null
            ? "BAKED" + (m4.autoReBake ? " (auto re-bake ON)" : "") + " -> " + AssetDatabase.GetAssetPath(m4.bakedMeshAsset)
            : "LIVE (auto-regenerate σε κάθε αλλαγή)";

        string modeInfo; string warn = "";
        if (m4.meshSourceMode == Mesh4.MeshSourceMode.Polygon)
        {
            int npts = m4.outlinePoints != null ? m4.outlinePoints.Count : 0;
            modeInfo = "Generation: Polygon (" + npts + " points)";
            if (npts < 3) warn = "\n⚠ Χρειάζονται ≥3 Outline Points!";
        }
        else
        {
            modeInfo = "Generation: " + m4.meshGenerationMode +
                (m4.meshGenerationMode == Mesh4.MeshGenerationMode.Grid ? $" (downsampling {m4.downsampling})" : "");
            if (m4.warpCamera == null) warn = "\n⚠ Λείπει warpCamera!";
        }

        string spawnInfo = m4.meshSourceMode == Mesh4.MeshSourceMode.Polygon
            ? "Spawn world (" + m4.polygonSpawnPoint.x.ToString("0.##") + ", " + m4.polygonSpawnPoint.z.ToString("0.##") + ")"
            : "Spawn UV (" + m4.playerSpawnUV.x.ToString("0.##") + ", " + m4.playerSpawnUV.y.ToString("0.##") + ")";

        EditorGUILayout.HelpBox(
            bakeInfo + "\n" + modeInfo + "\n" +
            "Mesh: " + m4.statVerts + " verts / " + m4.statTris + " tris\n" + spawnInfo + warn,
            string.IsNullOrEmpty(warn) ? MessageType.Info : MessageType.Warning);
    }

    void OnSceneGUI()
    {
        var m4 = (Mesh4)target;
        if (m4.meshSourceMode == Mesh4.MeshSourceMode.Mask && m4.warpCamera == null) return;

        // ── Outline point handles (Polygon mode) ──
        if (m4.meshSourceMode == Mesh4.MeshSourceMode.Polygon && m4.outlinePoints != null)
        {
            // Edge lines (κλειστό loop) για οπτική σειρά.
            Handles.color = new Color(0f, 1f, 0.4f, 0.8f);
            int n = m4.outlinePoints.Count;
            for (int i = 0; i < n; i++)
            {
                Vector3 a = m4.transform.TransformPoint(m4.outlinePoints[i]);
                Vector3 b = m4.transform.TransformPoint(m4.outlinePoints[(i + 1) % n]);
                Handles.DrawLine(a, b);
            }

            for (int i = 0; i < n; i++)
            {
                Vector3 wp = m4.transform.TransformPoint(m4.outlinePoints[i]);
                float hs = HandleUtility.GetHandleSize(wp) * 0.09f;
                Handles.color = Color.cyan;
                EditorGUI.BeginChangeCheck();
#if UNITY_2022_1_OR_NEWER
                Vector3 moved = Handles.FreeMoveHandle(wp, hs, Vector3.zero, Handles.DotHandleCap);
#else
                Vector3 moved = Handles.FreeMoveHandle(wp, Quaternion.identity, hs, Vector3.zero, Handles.DotHandleCap);
#endif
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(m4, "Move Outline Point");
                    // Κράτα το σημείο πάνω στο floorY plane (world), μετά back σε local.
                    Vector3 snapped = new Vector3(moved.x, m4.floorY, moved.z);
                    m4.outlinePoints[i] = m4.transform.InverseTransformPoint(snapped);
                    EditorUtility.SetDirty(m4);
                }
                Handles.Label(wp + Vector3.up * hs * 2.5f, i.ToString());
            }
        }

        // ── Spawn handle ──
        Vector3 pos = m4.GetSpawnWorldPosition();
        float size = HandleUtility.GetHandleSize(pos) * 0.15f;
        Handles.color = Color.yellow;
        EditorGUI.BeginChangeCheck();
#if UNITY_2022_1_OR_NEWER
        Vector3 np = Handles.FreeMoveHandle(pos, size, Vector3.zero, Handles.SphereHandleCap);
#else
        Vector3 np = Handles.FreeMoveHandle(pos, Quaternion.identity, size, Vector3.zero, Handles.SphereHandleCap);
#endif
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(m4, "Move Spawn Point");
            if (m4.meshSourceMode == Mesh4.MeshSourceMode.Polygon)
            {
                m4.polygonSpawnPoint = new Vector3(np.x, m4.floorY, np.z);
            }
            else
            {
                Vector2 uv = m4.WorldToUV(np);
                m4.playerSpawnUV = new Vector2(Mathf.Clamp01(uv.x), Mathf.Clamp01(uv.y));
            }
            EditorUtility.SetDirty(m4);
        }
        Handles.Label(pos + Vector3.up * size * 2f, "Player Spawn");
    }
}
#endif
