using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.SceneManagement;
using UnityEngine;
#if BSE_VRCSDK
using VRC.SDK3.Avatars.Components;
#endif
using Object = UnityEngine.Object;
using static AzipaWorks.BlendshapeEditor.BseLocalization;

namespace AzipaWorks.BlendshapeEditor
{
    /// <summary>
    /// 既存シェイプキーを組み合わせて新しいシェイプキーを作るウィンドウ。
    /// 「作成」タブで 1 つずつ作り、「管理」タブ（作成シェイプ一覧・並び順・アバター管理・設定）で確認・反映・設定を行う。
    /// 保存時は常に元メッシュから全シェイプを作り直し、設定（レシピ）を作成メッシュと同じ .asset に保存する。
    /// </summary>
    internal class BlendshapeEditorWindow : EditorWindow
    {
        private const string Title = "Blendshape Editor";

        /// <summary>ツールのバージョン（package.json の version と合わせる）</summary>
        public const string Version = "1.1.0";
        private const string DefaultFolder = "Assets/BlendshapeEditor_Generated";
        private const string DefaultSuffix = "_orig";
        private const string TopToken = "\u0001TOP";

        private enum Tab
        {
            Create, // シェイプを 1 つずつ作る
            Manage, // 作成したシェイプ・アバター・設定の管理（サブタブで切り替え）
        }

        private enum ManageTab
        {
            Shapes, // 作成したシェイプの一覧（確認・編集・削除）
            Avatars, // アバターへの反映・元に戻す
            Settings, // 顔のデータの情報・退避名・作り直し
            Order, // シェイプキーの並び順
        }

        [SerializeField] private SkinnedMeshRenderer targetRenderer;
        [SerializeField] private Mesh baseMesh;
        [SerializeField] private ShapeDefinition draft = new ShapeDefinition();
        [SerializeField] private Tab tab = Tab.Create;
        [SerializeField] private ManageTab manageTab = ManageTab.Shapes;
        [SerializeField] private bool previewEnabled = true;
        [SerializeField] private float previewValue = 100f;
        [SerializeField] private bool nonZeroOnly;
        [SerializeField] private bool overLimit;
        [SerializeField] private int overLimitMax = 300;
        [SerializeField] private string suffixEdit = "";
        [SerializeField] private bool editExisting;
        [SerializeField] private string insertAfter = "";
        [SerializeField] private string headerEdit = ""; // 追加する位置（"" = 既定の位置、TopToken = 先頭、それ以外 = そのシェイプの下）
        private readonly HashSet<string> _expandedShapes = new HashSet<string>(); // 作成シェイプ一覧タブで詳細を開いているシェイプ

        private Vector2 _scroll;
        private bool _dirty;
        private bool _weightOnly; // プレビュー値だけ変わった（メッシュの作り直し不要）
        private double _lastBuildTime;

        // 重い処理の結果を使い回すためのキャッシュ
        private Mesh _recipeCachedFor;
        private ShapeRecipe _recipeCache;
        private bool _recipeCacheValid;
        private readonly Dictionary<int, ShapeBakeCore.Frame[]> _frameCache = new Dictionary<int, ShapeBakeCore.Frame[]>();
        private Mesh _frameCacheMesh;
        private float[] _maskCache;
        private (Mesh mesh, Transform t, float width) _maskKey;
        private Vector3[] _dv, _dn, _dt;

        // 割り当て中のメッシュと元メッシュの同期
        private Mesh _syncedAssigned;
        private bool _synced;
        private bool _baseMissing;

        [MenuItem("Tools/Azipa Tools/Blendshape Editor", false, 914)]
        private static void Open()
        {
            var w = GetWindow<BlendshapeEditorWindow>(Title);
            var smr = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<SkinnedMeshRenderer>()
                : null;
            if (smr != null && w.targetRenderer == null && IsSceneRenderer(smr)) w.Load(smr);
            w.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(Title, Icon);
            wantsMouseMove = true;
            Preview.Owner = this;
            _dirty = true;
            EditorApplication.hierarchyChanged += InvalidateDuplicates;
            EditorApplication.projectChanged += InvalidateDuplicates;
            Undo.undoRedoPerformed += InvalidateDuplicates;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= InvalidateDuplicates;
            EditorApplication.projectChanged -= InvalidateDuplicates;
            Undo.undoRedoPerformed -= InvalidateDuplicates;
            if (Preview.Owner == this) Preview.Owner = null;
            Preview.Restore();
        }

        public void MarkDirty() => _dirty = true;

        private void Update()
        {
            // ライセンスが無いときはプレビューも含めて何もしない
            if (!BlendshapeEditorLicense.IsLicensed())
            {
                if (Preview.IsShowing) Preview.Restore();
                return;
            }

            SyncBase();
            if (_weightOnly && !_dirty)
            {
                _weightOnly = false;
                if (Preview.IsShowing) Preview.SetWeight(PreviewWeight());
            }

            if (!_dirty) return;
            // スライダーのドラッグ中などに作り直しが連続しないよう間引く
            if (EditorApplication.timeSinceStartup - _lastBuildTime < 0.08) return;
            _dirty = false;
            _weightOnly = false;
            _lastBuildTime = EditorApplication.timeSinceStartup;
            UpdatePreview();
            Repaint(); // プレビュー状態の帯を更新
        }

        // ------------------------------------------------------------------ state

        /// <summary>プレビュー中でも差し替え前の（実際に割り当てられている）メッシュを返す</summary>
        private Mesh AssignedMesh => targetRenderer == null
            ? null
            : Preview.OriginalOf(targetRenderer) ?? targetRenderer.sharedMesh;

        /// <summary>割り当て中のメッシュがこのツールで作ったものならそのレシピ</summary>
        private ShapeRecipe Recipe
        {
            get
            {
                var assigned = AssignedMesh;
                if (!_recipeCacheValid || _recipeCachedFor != assigned)
                {
                    _recipeCachedFor = assigned;
                    _recipeCache = ShapeRecipe.Find(assigned);
                    _recipeCacheValid = true;
                }

                return _recipeCache;
            }
        }

        private void InvalidateRecipe()
        {
            _recipeCacheValid = false;
            _backupPlan = null;
        }

        private string Suffix => Recipe != null ? ShapeBakeCore.NormalizeSuffix(Recipe.backupSuffix) : DefaultSuffix;

        private List<ShapeDefinition> SavedShapes =>
            Recipe != null ? Recipe.shapes : new List<ShapeDefinition>();

        private static string Key(ShapeDefinition d) => d.outputName.Trim();

        /// <summary>作成中のシェイプと同じ名前の作成済みシェイプ（あれば更新になる）</summary>
        private ShapeDefinition SavedWithSameName =>
            string.IsNullOrWhiteSpace(draft.outputName) ? null : SavedShapes.FirstOrDefault(s => Key(s) == Key(draft));

        /// <summary>作成済みシェイプに作成中のシェイプを加えた（同名は置き換えた）一覧</summary>
        private List<ShapeDefinition> ShapesWithDraft()
        {
            var list = SavedShapes.Where(s => Key(s) != Key(draft)).Select(s => s.Clone()).ToList();
            list.Add(draft.Clone());
            return list;
        }

        /// <summary>レンダラーを読み込む。作成済みメッシュならレシピから元メッシュを復元する</summary>
        private void Load(SkinnedMeshRenderer r)
        {
            Preview.Restore();
            targetRenderer = r;
            _synced = false;
            baseMesh = null;
            InvalidateDuplicates();
            SyncBase();
            draft = new ShapeDefinition();
            _dirty = true;
        }

        /// <summary>
        /// 割り当て中のメッシュが外部で変わった（Undo・手動差し替えなど）ら、元メッシュとレシピを読み直す。
        /// 古い元メッシュのまま作成してしまうのを防ぐ。
        /// </summary>
        private void SyncBase()
        {
            if (targetRenderer == null)
            {
                _syncedAssigned = null;
                _synced = false;
                _baseMissing = false;
                return;
            }

            var assigned = AssignedMesh;
            if (_synced && assigned == _syncedAssigned) return;
            _synced = true;
            _syncedAssigned = assigned;
            InvalidateRecipe();
            var recipe = Recipe;
            _baseMissing = recipe != null && recipe.baseMesh == null;
            baseMesh = recipe != null ? recipe.baseMesh : assigned;
            suffixEdit = Suffix;
            _dirty = true;
            Repaint();
        }

        // ------------------------------------------------------------------ 他のアバターに反映
        //
        // 目的: 同じ FBX を共有していたアバターに、今のマージ後のファイルを反映する方法がわからない人のための救済。
        // シーン上のアバターをすべて一覧にし、各アバターの「同じオブジェクト」（例: Body）が使っているファイルと
        // 反映状態を見せて、今のファイルを反映するかどうかだけを選んでもらう。

        private enum ReflectState
        {
            Applied, // 今のファイルを使っている
            SameFile, // 元のファイル（シェイプを作る前の顔）のまま
            DifferentFile, // 元とは異なるファイルを使っている（頂点数は同じなので反映できる）
            Unavailable, // 反映できない（同じオブジェクトが無い・頂点数が違う など）
        }

        private class AvatarEntry
        {
            public GameObject avatar;
            public SkinnedMeshRenderer renderer;
            public ReflectState state;
            public string reason;
            public bool isSelf; // 編集中のアバター
            public string rendererId; // 反映前の記録を探すための ID
            public EyelidState eyelid; // 瞬き・視線（番号で参照）の状態
            public int eyelidFrom; // ずれている場合、どの時点の並びで設定されているか
        }

        private enum EyelidState
        {
            NotUsed, // 瞬き・視線にこのシェイプを使っていない
            Ok,
            Shifted, // 並び替え前の番号のまま（付け直しが必要）
            Unknown, // 記録が無く判断できない
        }

        /// <summary>Avatar Descriptor の瞬き・視線が、今の並びに合っているか</summary>
        private EyelidState EyelidStatus(SkinnedMeshRenderer r, out int from) => JudgeEyelids(Recipe, r, out from);

        /// <summary>
        /// 瞬き・視線がずれているかを判定する。並びの版番号ではなく、Descriptor が使っている番号ごとに
        /// 「設定した時点の並びで指していた名前」と「今の並びで指している名前」を比べる。
        /// （並び替えて元に戻した場合や、瞬き・視線と関係ない場所だけ並び替えた場合は、ずれていない）
        /// </summary>
        private static EyelidState JudgeEyelids(ShapeRecipe recipe, SkinnedMeshRenderer r, out int from)
        {
            from = 0;
            var id = EyelidOwnerId(r);
            if (recipe == null || id == null) return EyelidState.NotUsed;
            var v = recipe.AvatarVersion(id);
            if (v == null) return recipe.orderVersion == 0 ? EyelidState.Ok : EyelidState.Unknown;
            from = v.Value;
            if (v.Value == recipe.orderVersion) return EyelidState.Ok;
            var snap = recipe.Snapshot(v.Value);
            if (snap == null) return EyelidState.Unknown;

            var current = ShapeNames(r.sharedMesh);
            foreach (var idx in EyelidIndices(r))
            {
                if (idx < 0) continue;
                if (idx >= snap.names.Count) return EyelidState.Unknown;
                var now = idx < current.Length ? current[idx] : null;
                if (snap.names[idx] != now) return EyelidState.Shifted;
            }

            return EyelidState.Ok;
        }

        /// <summary>Avatar Descriptor の瞬き・視線が使っているシェイプの番号</summary>
        private static int[] EyelidIndices(SkinnedMeshRenderer r)
        {
#if BSE_VRCSDK
            var d = r != null ? r.GetComponentInParent<VRCAvatarDescriptor>(true) : null;
            if (d == null || d.customEyeLookSettings.eyelidsSkinnedMesh != r) return Array.Empty<int>();
            return d.customEyeLookSettings.eyelidsBlendshapes ?? Array.Empty<int>();
#else
            return Array.Empty<int>();
#endif
        }

        /// <summary>ずれている瞬き・視線を、設定した時点の並びから名前で付け直す</summary>
        private void FixEyelids(List<AvatarEntry> entries)
        {
            var recipe = Recipe;
            if (recipe == null || entries.Count == 0) return;
            var warnings = new List<string>();
            Undo.SetCurrentGroupName(Title + T(" (瞬き・視線の付け直し)"));
            int group = Undo.GetCurrentGroup();
            foreach (var e in entries)
            {
                var snap = recipe.Snapshot(e.eyelidFrom);
                if (snap == null) continue;
                RemapEyelids(e.renderer, snap.names.ToArray(), warnings);
                recipe.SetAvatarVersion(EyelidOwnerId(e.renderer), recipe.orderVersion);
            }

            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
            Undo.CollapseUndoOperations(group);
            foreach (var w in warnings) Debug.LogWarning($"[{Title}] {w}");
            Debug.Log(T("[{0}] {1} 体のアバターの瞬き・視線を付け直しました。", Title, entries.Count));
            foreach (var e in entries) _checked.Remove(e.avatar);
            InvalidateDuplicates();
        }

        private List<AvatarEntry> _avatars;
        private List<(Mesh mesh, ShapeRecipe recipe)> _allRecipes;
        private readonly Dictionary<Mesh, bool> _sameFaceCache = new Dictionary<Mesh, bool>();
        private readonly HashSet<GameObject> _checked = new HashSet<GameObject>();

        private void InvalidateDuplicates()
        {
            _avatars = null;
            _allRecipes = null;
            _sameFaceCache.Clear();
            Repaint();
        }

        /// <summary>プロジェクト内の保存ファイル（このツールで作ったもの）すべて</summary>
        private List<(Mesh mesh, ShapeRecipe recipe)> AllRecipes()
        {
            if (_allRecipes != null) return _allRecipes;
            _allRecipes = new List<(Mesh, ShapeRecipe)>();
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(ShapeRecipe)))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mesh = AssetDatabase.LoadMainAssetAtPath(path) as Mesh;
                var recipe = AssetDatabase.LoadAllAssetsAtPath(path).OfType<ShapeRecipe>().FirstOrDefault();
                if (mesh != null && recipe != null && recipe.baseMesh != null) _allRecipes.Add((mesh, recipe));
            }

            return _allRecipes;
        }

        /// <summary>
        /// 今のファイルをそのまま使えるか。頂点の数と並びの単位（サブメッシュ数）が同じなら使える。
        /// </summary>
        private bool IsCompatible(Mesh mesh) =>
            mesh != null && baseMesh != null &&
            (mesh == baseMesh || (mesh.vertexCount == baseMesh.vertexCount && mesh.subMeshCount == baseMesh.subMeshCount));

        /// <summary>シェイプキー名の 8 割以上が一致すれば、同じアバターの顔とみなす</summary>
        private bool IsSameFace(Mesh face)
        {
            if (face == null || baseMesh == null) return false;
            if (face == baseMesh) return true;
            if (_sameFaceCache.TryGetValue(face, out var cached)) return cached;
            var mine = new HashSet<string>(ShapeNames(baseMesh));
            var theirs = ShapeNames(face);
            int overlap = theirs.Count(mine.Contains);
            bool same = theirs.Length > 0 && mine.Count > 0 &&
                        overlap >= mine.Count * 0.8f && overlap >= theirs.Length * 0.8f;
            _sameFaceCache[face] = same;
            return same;
        }

        /// <summary>ユーザーが置いたシーン上のレンダラー（NDMF などのプレビュー用の非表示オブジェクトは除く）</summary>
        private static bool IsUserRenderer(SkinnedMeshRenderer r) =>
            IsSceneRenderer(r) && r.gameObject.hideFlags == HideFlags.None && (r.hideFlags & HideFlags.HideInHierarchy) == 0 &&
            !EditorSceneManager.IsPreviewScene(r.gameObject.scene);

        private static bool IsUserObject(GameObject go) =>
            go != null && go.scene.IsValid() && !EditorUtility.IsPersistent(go) && go.hideFlags == HideFlags.None &&
            !EditorSceneManager.IsPreviewScene(go.scene);

        /// <summary>開いているシーンのアバター（Avatar Descriptor、無ければ Humanoid の Animator を持つオブジェクト）</summary>
        private static List<GameObject> SceneAvatars()
        {
#if BSE_VRCSDK
            var list = Resources.FindObjectsOfTypeAll<VRCAvatarDescriptor>().Select(d => d.gameObject);
#else
            var list = Resources.FindObjectsOfTypeAll<Animator>().Where(a => a.avatar != null && a.avatar.isHuman)
                .Select(a => a.gameObject);
#endif
            return list.Where(IsUserObject).Distinct().ToList();
        }

        /// <summary>
        /// 他のアバターの中から、対象メッシュと「同じオブジェクト」を探す。
        /// アバター内の階層パスが同じもの → 名前が同じもの → シェイプキー構成が同じもの、の順に探す。
        /// </summary>
        private SkinnedMeshRenderer FindSameObject(GameObject avatar)
        {
            var targetRoot = ShapeBakeCore.FindAvatarRoot(targetRenderer.transform);
            var relPath = AnimationUtility.CalculateTransformPath(targetRenderer.transform, targetRoot);
            var t = avatar.transform.Find(relPath);
            if (t != null && t.TryGetComponent<SkinnedMeshRenderer>(out var byPath) && IsUserRenderer(byPath)) return byPath;

            var all = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(IsUserRenderer).ToList();
            var byName = all.FirstOrDefault(r => r.name == targetRenderer.name);
            if (byName != null) return byName;

            return all.FirstOrDefault(r =>
            {
                var m = r.sharedMesh;
                if (m == null) return false;
                var rec = ShapeRecipe.Find(m);
                return IsSameFace(rec != null ? rec.baseMesh : m);
            });
        }

        /// <summary>シーンの全アバター（編集中のアバター以外）と、その「同じオブジェクト」の反映状態</summary>
        private List<AvatarEntry> Avatars()
        {
            if (_avatars != null) return _avatars;
            _avatars = new List<AvatarEntry>();
            if (targetRenderer == null || baseMesh == null) return _avatars;

            var file = AssignedMesh;
            var myAvatar = ShapeBakeCore.FindAvatarRoot(targetRenderer.transform).gameObject;
            var self = new AvatarEntry
            {
                avatar = myAvatar, renderer = targetRenderer, isSelf = true,
                state = Recipe != null ? ReflectState.Applied : ReflectState.SameFile,
            };
            foreach (var avatar in SceneAvatars())
            {
                if (avatar == myAvatar) continue;
                var e = new AvatarEntry { avatar = avatar, renderer = FindSameObject(avatar) };
                var m = e.renderer != null ? e.renderer.sharedMesh : null;
                if (e.renderer == null)
                {
                    e.state = ReflectState.Unavailable;
                    e.reason = T("同じオブジェクト（{0}）が見つかりません", targetRenderer.name);
                }
                else if (m == file) e.state = ReflectState.Applied;
                else if (m == null)
                {
                    e.state = ReflectState.Unavailable;
                    e.reason = T("メッシュが設定されていません");
                }
                else if (!IsCompatible(m))
                {
                    e.state = ReflectState.Unavailable;
                    e.reason = T("顔のデータの形（頂点数）が違うため反映できません");
                }
                else e.state = m == baseMesh ? ReflectState.SameFile : ReflectState.DifferentFile;

                _avatars.Add(e);
            }

            _avatars.Add(self);
            foreach (var e in _avatars) e.rendererId = e.renderer != null ? RendererId(e.renderer) : null;
            foreach (var e in _avatars)
                e.eyelid = e.state == ReflectState.Applied ? EyelidStatus(e.renderer, out e.eyelidFrom) : EyelidState.NotUsed;
            _avatars.Sort((a, b) => CompareHierarchyOrder(a.avatar.transform, b.avatar.transform));
            return _avatars;
        }

        /// <summary>Hierarchy の表示順で比べる（シーンの順 → 親から順に兄弟の並び）</summary>
        private static int CompareHierarchyOrder(Transform a, Transform b)
        {
            int sa = SceneOrder(a.gameObject.scene), sb = SceneOrder(b.gameObject.scene);
            if (sa != sb) return sa.CompareTo(sb);
            var pa = SiblingPath(a);
            var pb = SiblingPath(b);
            for (int i = 0; i < Mathf.Min(pa.Count, pb.Count); i++)
                if (pa[i] != pb[i])
                    return pa[i].CompareTo(pb[i]);
            return pa.Count.CompareTo(pb.Count);
        }

        private static int SceneOrder(UnityEngine.SceneManagement.Scene scene)
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i) == scene)
                    return i;
            return int.MaxValue;
        }

        private static List<int> SiblingPath(Transform t)
        {
            var path = new List<int>();
            for (; t != null; t = t.parent) path.Insert(0, t.GetSiblingIndex());
            return path;
        }

        /// <summary>ファイル名（FBX・改変メッシュなど）。メッシュ名が違えば併記する</summary>
        private static string FaceFileLabel(Mesh face)
        {
            if (face == null) return T("（なし）");
            var path = AssetDatabase.GetAssetPath(face);
            if (string.IsNullOrEmpty(path)) return face.name;
            var file = Path.GetFileName(path);
            return Path.GetFileNameWithoutExtension(path) == face.name ? file : $"{file}（{face.name}）";
        }

        private static string ShapeListText(ShapeRecipe recipe)
        {
            var names = recipe.shapes.Select(d => d.outputName.Trim()).Where(n => n.Length > 0).ToList();
            if (names.Count == 0) return T("シェイプなし");
            return names.Count <= 4
                ? string.Join("、", names)
                : string.Join("、", names.Take(4)) + T(" ほか {0} 個", names.Count - 4);
        }

        /// <summary>
        /// 保存ファイルをレンダラーに割り当てる。値はシェイプ名で引き継ぎ、置き換えた元のシェイプの値は退避先へ移す。
        /// Avatar Descriptor の瞬き・視線も名前で付け直す。
        /// </summary>
        private static void ApplyFile(SkinnedMeshRenderer r, Mesh file, ShapeRecipe recipe, List<string> warnings)
        {
            var current = r.sharedMesh;
            var oldRecipe = ShapeRecipe.Find(current);
            var oldNames = ShapeNames(current);
            var weights = RemapBackupWeights(WeightsByName(r),
                oldRecipe != null && oldRecipe.baseMesh != null
                    ? ShapeBakeCore.PlanBackups(oldRecipe)
                    : new List<(string, string)>(),
                ShapeBakeCore.PlanBackups(recipe));

            Undo.RecordObject(r, Title + T(" (反映)"));
            r.sharedMesh = file;
            for (int i = 0; i < file.blendShapeCount; i++)
                r.SetBlendShapeWeight(i, weights.TryGetValue(file.GetBlendShapeName(i), out var w) ? w : 0f);
            PrefabUtility.RecordPrefabInstancePropertyModifications(r);
            RemapEyelids(r, oldNames, warnings);
            var id = EyelidOwnerId(r);
            if (id != null)
            {
                recipe.SetAvatarVersion(id, recipe.orderVersion);
                EditorUtility.SetDirty(recipe);
            }
        }

        private static string RendererId(SkinnedMeshRenderer r) =>
            r != null ? GlobalObjectId.GetGlobalObjectIdSlow(r).ToString() : null;

        private static SkinnedMeshRenderer ResolveRenderer(string id)
        {
            if (string.IsNullOrEmpty(id) || !GlobalObjectId.TryParse(id, out var gid)) return null;
            var r = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) as SkinnedMeshRenderer;
            return IsSceneRenderer(r) ? r : null;
        }

        /// <summary>反映する直前の状態（使っていたメッシュ・各シェイプの値・瞬き/視線の設定）を記録する</summary>
        private static RestorePoint CaptureRestorePoint(SkinnedMeshRenderer r)
        {
            var p = new RestorePoint
            {
                rendererId = RendererId(r),
                avatarName = ShapeBakeCore.FindAvatarRoot(r.transform).name,
                mesh = r.sharedMesh,
                weights = WeightsByName(r).Select(kv => new WeightEntry { name = kv.Key, value = kv.Value }).ToList(),
            };
#if BSE_VRCSDK
            var d = r.GetComponentInParent<VRCAvatarDescriptor>(true);
            if (d != null && d.customEyeLookSettings.eyelidsSkinnedMesh == r && d.customEyeLookSettings.eyelidsBlendshapes != null)
            {
                p.hasEyelids = true;
                p.descriptorId = GlobalObjectId.GetGlobalObjectIdSlow(d).ToString();
                p.eyelids = (int[])d.customEyeLookSettings.eyelidsBlendshapes.Clone();
            }
#endif
            return p;
        }

        /// <summary>記録どおりの「反映前の状態」に戻す。記録したメッシュが無くなっていれば false</summary>
        private static bool RestoreFromPoint(SkinnedMeshRenderer r, RestorePoint p, ShapeRecipe recipe, List<string> warnings)
        {
            if (p.mesh == null)
            {
                warnings.Add(T("「{0}」の反映前のファイルが見つからないため、記録どおりには戻せませんでした。", p.avatarName));
                return false;
            }

            var id = EyelidOwnerId(r);
            if (recipe != null && id != null) recipe.RemoveAvatar(id);

            Undo.RecordObject(r, Title + T(" (反映前に戻す)"));
            r.sharedMesh = p.mesh;
            var weights = p.weights.GroupBy(x => x.name).ToDictionary(g => g.Key, g => g.Last().value);
            for (int i = 0; i < p.mesh.blendShapeCount; i++)
                r.SetBlendShapeWeight(i, weights.TryGetValue(p.mesh.GetBlendShapeName(i), out var w) ? w : 0f);
            PrefabUtility.RecordPrefabInstancePropertyModifications(r);
#if BSE_VRCSDK
            if (p.hasEyelids)
            {
                var d = r.GetComponentInParent<VRCAvatarDescriptor>(true);
                if (d != null)
                {
                    Undo.RecordObject(d, Title + T(" (反映前に戻す)"));
                    var eye = d.customEyeLookSettings;
                    eye.eyelidsSkinnedMesh = r;
                    eye.eyelidsBlendshapes = (int[])p.eyelids.Clone();
                    d.customEyeLookSettings = eye;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(d);
                }
            }
#endif
            p.restored = true;
            return true;
        }

        /// <summary>「反映前に戻す」で戻る先の表示</summary>
        private string RevertDestinationLabel(AvatarEntry e)
        {
            var point = Recipe?.LatestRestorePoint(e.rendererId);
            if (point != null)
                return point.mesh != null ? FaceFileLabel(point.mesh) : T("（反映前のファイルが見つかりません。作る前の顔に戻します）");
            return T("{0}（反映前の記録なし・作る前の顔）", FaceFileLabel(baseMesh));
        }

        /// <summary>今のファイルを、チェックしたアバターの「同じオブジェクト」に反映する</summary>
        private void ReflectTo(List<AvatarEntry> entries)
        {
            var file = AssignedMesh;
            var recipe = Recipe;
            if (recipe == null || entries.Count == 0) return;
            Preview.Restore();
            var warnings = new List<string>();
            Undo.SetCurrentGroupName(Title + T(" (反映)"));
            int group = Undo.GetCurrentGroup();

            // 反映前の状態を記録してから反映する（Ctrl+Z で反映を取り消すと、この記録も一緒に消える）
            Undo.RecordObject(recipe, Title + T(" (反映)"));
            var history = new ReflectHistoryEntry { id = Guid.NewGuid().ToString("N"), ticks = DateTime.Now.Ticks };
            foreach (var e in entries) history.points.Add(CaptureRestorePoint(e.renderer));
            recipe.AddReflect(history);
            EditorUtility.SetDirty(recipe);

            foreach (var e in entries) ApplyFile(e.renderer, file, recipe, warnings);
            AssetDatabase.SaveAssets();
            Undo.CollapseUndoOperations(group);
            foreach (var w in warnings) Debug.LogWarning($"[{Title}] {w}");
            Debug.Log(T("[{0}] {1} 体のアバターに反映しました。", Title, entries.Count));
            foreach (var e in entries) _checked.Remove(e.avatar);
            InvalidateDuplicates();
            _dirty = true;
        }

        /// <summary>チェックしたアバターを、反映前の状態に戻す（記録が無ければ作る前の顔）</summary>
        private void RevertEntries(List<AvatarEntry> entries)
        {
            if (entries.Count == 0) return;
            Preview.Restore();
            RevertRenderers(entries.Select(e => e.renderer).ToList());
            foreach (var e in entries) _checked.Remove(e.avatar);
            InvalidateDuplicates();
        }

        /// <summary>このアバターの顔用に作った保存ファイル（今使っているものを含む）</summary>
        private List<(Mesh mesh, ShapeRecipe recipe)> SavedFilesForThisFace() =>
            AllRecipes()
                .Where(x => IsCompatible(x.recipe.baseMesh) && IsSameFace(x.recipe.baseMesh) && IsCompatible(x.mesh))
                .ToList();

        /// <summary>プロジェクト内でこのファイルを使っているシーン・プレハブ（中断されたら null）</summary>
        private static List<string> FindProjectReferences(string assetPath)
        {
            var paths = AssetDatabase.FindAssets("t:Scene").Concat(AssetDatabase.FindAssets("t:Prefab"))
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(x => x.StartsWith("Assets/")).Distinct().ToList();
            var found = new List<string>();
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    if (i % 20 == 0 && EditorUtility.DisplayCancelableProgressBar(Title, T("使っている場所を調べています…"),
                            (float)i / paths.Count))
                        return null;
                    if (AssetDatabase.GetDependencies(paths[i], false).Contains(assetPath)) found.Add(paths[i]);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            return found;
        }

        /// <summary>
        /// 不要になった保存ファイルを削除する（ごみ箱へ移動）。開いているシーンで使っているファイルは削除しない。
        /// シーン・プレハブから参照されている場合は警告してから削除する。
        /// </summary>
        private void DeleteSavedFile(Mesh mesh)
        {
            var path = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path)) return;
            var file = Path.GetFileName(path);

            var users = RenderersUsing(mesh);
            if (Preview.OriginalOf(targetRenderer) == mesh && !users.Contains(targetRenderer)) users.Add(targetRenderer);
            if (users.Count > 0)
            {
                EditorUtility.DisplayDialog(Title,
                    T("「{0}」は次のアバターが使っているため削除できません。\n\n", file) +
                    string.Join("\n", users.Take(10).Select(u => T("・") + ShapeBakeCore.FindAvatarRoot(u.transform).name)) +
                    (users.Count > 10 ? T("\nほか {0} 体", users.Count - 10) : "") +
                    T("\n\n先に「管理 › アバター管理」で「反映前に戻す」か、別のファイルを反映してください。"),
                    "OK");
                return;
            }

            var refs = FindProjectReferences(path);
            if (refs == null) return; // 中断

            string message = T("「{0}」を削除しますか？\n", file) +
                             T("このファイルで作ったシェイプの設定も一緒に削除されます。ファイルはごみ箱に移動します。");
            if (refs.Count > 0)
                message += T("\n\n⚠ 次のシーン・プレハブがこのファイルを使っています。削除するとそのメッシュが表示されなくなります。\n") +
                           string.Join("\n", refs.Take(10).Select(x => T("・") + x)) +
                           (refs.Count > 10 ? T("\nほか {0} 件", refs.Count - 10) : "");
            if (!EditorUtility.DisplayDialog(Title, message, refs.Count > 0 ? T("それでも削除する") : T("削除する"), T("キャンセル")))
                return;

            if (AssetDatabase.MoveAssetToTrash(path)) Debug.Log(T("[{0}] {1} をごみ箱に移動しました。", Title, path));
            else Debug.LogError(T("[{0}] {1} を削除できませんでした。", Title, path));
            InvalidateRecipe();
            InvalidateDuplicates();
            _synced = false;
            _dirty = true;
        }

        /// <summary>保存済みファイルの 1 行（使う・削除）</summary>
        private void DrawSavedFileRow(Mesh mesh, ShapeRecipe recipe, bool canUse)
        {
            bool inUse = mesh == AssignedMesh;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    new GUIContent($"{FaceFileLabel(mesh)}：{ShapeListText(recipe)}" + (inUse ? T("（使用中）") : ""),
                        AssetDatabase.GetAssetPath(mesh)),
                    EditorStyles.wordWrappedMiniLabel);
                if (canUse)
                    using (new EditorGUI.DisabledScope(inUse))
                        if (GUILayout.Button(new GUIContent(T("このアバターに使う"), T("保存済みのファイルをこのアバターに割り当てます")),
                                GUILayout.Width(120)))
                        {
                            UseSavedFile(mesh, recipe);
                            GUIUtility.ExitGUI();
                        }

                if (GUILayout.Button(new GUIContent(T("削除"), T("不要になったファイルを削除します（ごみ箱へ移動）")), GUILayout.Width(44)))
                {
                    DeleteSavedFile(mesh);
                    GUIUtility.ExitGUI();
                }
            }
        }

        /// <summary>保存済みファイルを、今の対象メッシュに使う（同じシェイプを作り直さずに済むように）</summary>
        private void UseSavedFile(Mesh file, ShapeRecipe recipe)
        {
            Preview.Restore();
            var warnings = new List<string>();
            Undo.RecordObject(recipe, Title + T(" (反映)"));
            recipe.AddReflect(new ReflectHistoryEntry
            {
                id = Guid.NewGuid().ToString("N"),
                ticks = DateTime.Now.Ticks,
                points = new List<RestorePoint> { CaptureRestorePoint(targetRenderer) },
            });
            EditorUtility.SetDirty(recipe);
            ApplyFile(targetRenderer, file, recipe, warnings);
            AssetDatabase.SaveAssets();
            foreach (var w in warnings) Debug.LogWarning($"[{Title}] {w}");
            InvalidateRecipe();
            InvalidateDuplicates();
            _synced = false;
            draft = new ShapeDefinition();
            _dirty = true;
        }

        /// <summary>
        /// まだシェイプを作っていない顔を開いたとき、同じアバター用に作ったファイルがあれば使うよう案内する
        /// （同じシェイプを作り直してしまうのを防ぐ）。
        /// </summary>
        private void DrawDuplicateNotice()
        {
            if (Recipe != null) return;
            var files = SavedFilesForThisFace();
            if (files.Count == 0) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(T("このアバター用に作ったシェイプがあります。もう一度作る必要はありません。"),
                    EditorStyles.wordWrappedLabel);
                foreach (var (mesh, recipe) in files) DrawSavedFileRow(mesh, recipe, true);
                EditorGUILayout.LabelField(T("不要になったファイルは「削除」で整理できます（「管理 › 設定」からも整理できます）。"),
                    EditorStyles.miniLabel);
            }
        }

        private static GUIStyle _shapeNameStyle, _badgeStyle, _warnStyle, _detailStyle;

        private static GUIStyle ShapeNameStyle => _shapeNameStyle ?? (_shapeNameStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Clip,
        });

        private static GUIStyle BadgeStyle => _badgeStyle ?? (_badgeStyle = new GUIStyle(EditorStyles.miniBoldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.white },
            padding = new RectOffset(0, 0, 0, 0),
        });

        private static GUIStyle WarnStyle => _warnStyle ?? (_warnStyle = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(1f, 0.75f, 0.2f) },
        });

        private static GUIStyle DetailStyle => _detailStyle ?? (_detailStyle = new GUIStyle
        {
            padding = new RectOffset(12, 4, 0, 4),
        });

        private static GUIStyle _rowTitle;

        private static GUIStyle RowTitle => _rowTitle ?? (_rowTitle = new GUIStyle(EditorStyles.label)
        {
            fontStyle = FontStyle.Bold,
            clipping = TextClipping.Clip,
        });

        /// <summary>
        /// アバタータブ：シーンの全アバター（編集中のアバターを含む）を一覧にして、
        /// 今のファイルを「反映する」か「反映前に戻す」かを選ぶ。
        /// </summary>
        private void DrawDuplicatesSection()
        {
            var file = AssignedMesh;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(T("反映するファイル"), GUILayout.Width(130));
                EditorGUILayout.LabelField(new GUIContent(FaceFileLabel(file), AssetDatabase.GetAssetPath(file)),
                    EditorStyles.miniBoldLabel);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent(T("作る前のファイル"), T("反映前の記録が無いアバターは、「反映前に戻す」とこのファイルになります")),
                    GUILayout.Width(130));
                EditorGUILayout.LabelField(new GUIContent(FaceFileLabel(baseMesh), AssetDatabase.GetAssetPath(baseMesh)),
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(new GUIContent(
                    T("チェックを入れて「反映」または「反映前に戻す」を押してください。"),
                    T("反映したアバターは、このあと更新した内容も自動で反映されます。どちらの操作も Undo で取り消せます。")),
                EditorStyles.wordWrappedMiniLabel);

            var list = Avatars();
            var reflectable = list.Where(e => e.state == ReflectState.SameFile || e.state == ReflectState.DifferentFile).ToList();
            var revertable = list.Where(e => e.state == ReflectState.Applied).ToList();
            _checked.RemoveWhere(go => go == null || list.All(e => e.avatar != go || e.state == ReflectState.Unavailable));

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(T("{0} 体（反映済み {1}・未反映 {2}）", list.Count, revertable.Count, reflectable.Count), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(reflectable.Count == 0))
                    if (GUILayout.Button(T("未反映をすべて選ぶ"), EditorStyles.miniButtonLeft, GUILayout.Width(110)))
                    {
                        _checked.Clear();
                        foreach (var e in reflectable) _checked.Add(e.avatar);
                    }

                using (new EditorGUI.DisabledScope(revertable.Count == 0))
                    if (GUILayout.Button(T("反映済みをすべて選ぶ"), EditorStyles.miniButtonMid, GUILayout.Width(120)))
                    {
                        _checked.Clear();
                        foreach (var e in revertable) _checked.Add(e.avatar);
                    }

                using (new EditorGUI.DisabledScope(_checked.Count == 0))
                    if (GUILayout.Button(T("選択を外す"), EditorStyles.miniButtonRight, GUILayout.Width(70)))
                        _checked.Clear();
            }

            foreach (var e in list) DrawAvatarRow(e);

            var toReflect = reflectable.Where(e => _checked.Contains(e.avatar)).ToList();
            var toRevert = revertable.Where(e => _checked.Contains(e.avatar)).ToList();
            var shiftedAll = list.Where(e => e.eyelid == EyelidState.Shifted).ToList();
            var toFix = shiftedAll.Where(e => _checked.Contains(e.avatar)).ToList();
            if (shiftedAll.Count > 0)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(
                        T("瞬き・視線の設定がずれているアバターが {0} 体あります。", shiftedAll.Count) +
                        T("Avatar Descriptor はシェイプを番号で参照しているため、並び替えでずれます。"),
                        EditorStyles.wordWrappedMiniLabel);
                    if (GUILayout.Button(T("選ぶ"), GUILayout.Width(44)))
                    {
                        _checked.Clear();
                        foreach (var e in shiftedAll) _checked.Add(e.avatar);
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(toReflect.Count == 0))
                    if (GUILayout.Button(new GUIContent(toReflect.Count > 0 ? T("反映（{0} 体）", toReflect.Count) : T("反映"),
                            T("チェックした未反映のアバターに、反映するファイルを割り当てます")), GUILayout.Height(30)))
                    {
                        var different = toReflect.Where(e => e.state == ReflectState.DifferentFile).ToList();
                        if (different.Count == 0 || EditorUtility.DisplayDialog(Title,
                                T("次のアバターは、作る前のファイルとは異なるファイルを使っています。反映すると顔のデータもこのファイルと同じになります。\n\n") +
                                string.Join("\n", different.Select(e => T("・{0}（{1}）", e.avatar.name, FaceFileLabel(e.renderer.sharedMesh)))) +
                                T("\n\n反映しますか？"),
                                T("反映する"), T("キャンセル")))
                            ReflectTo(toReflect);
                        GUIUtility.ExitGUI();
                    }

                using (new EditorGUI.DisabledScope(toRevert.Count == 0))
                    if (GUILayout.Button(new GUIContent(toRevert.Count > 0 ? T("反映前に戻す（{0} 体）", toRevert.Count) : T("反映前に戻す"),
                            T("チェックした反映済みのアバターを、反映する前の状態（使っていたファイル・値・瞬き/視線）に戻します。保存したファイルは消えません")),
                            GUILayout.Height(30)))
                    {
                        bool includesSelf = toRevert.Any(e => e.isSelf);
                        if (!includesSelf || EditorUtility.DisplayDialog(Title,
                                T("編集中のアバターも反映前に戻します。\n") +
                                T("戻したあとは、このウィンドウの「管理」タブは空になります（ファイルを割り当て直すと再び編集できます）。\n\n戻しますか？"),
                                T("戻す"), T("キャンセル")))
                            RevertEntries(toRevert);
                        GUIUtility.ExitGUI();
                    }
            }

            if (shiftedAll.Count > 0)
                using (new EditorGUI.DisabledScope(toFix.Count == 0))
                    if (GUILayout.Button(new GUIContent(toFix.Count > 0 ? T("瞬き・視線を付け直す（{0} 体）", toFix.Count) : T("瞬き・視線を付け直す"),
                            T("チェックしたアバターの Avatar Descriptor の瞬き・視線を、同じ名前のシェイプを指すように付け直します")),
                            GUILayout.Height(26)))
                    {
                        FixEyelids(toFix);
                        GUIUtility.ExitGUI();
                    }

            if (_checked.Count > 0 && toReflect.Count + toRevert.Count + toFix.Count < _checked.Count)
                EditorGUILayout.HelpBox(T("チェックしたアバターのうち、操作できないものは対象外になります。"), MessageType.None);

            DrawReflectHistory();
        }

        /// <summary>反映の履歴（いつ・どのアバターに反映したか）と、その回の反映の取り消し</summary>
        private void DrawReflectHistory()
        {
            var recipe = Recipe;
            if (recipe == null) return;
            Header(T("反映の履歴"));
            if (recipe.reflectHistory.Count == 0)
            {
                EditorGUILayout.LabelField(T("まだありません。"), EditorStyles.miniLabel);
                return;
            }

            ReflectHistoryEntry undo = null;
            for (int i = recipe.reflectHistory.Count - 1; i >= 0; i--)
            {
                var h = recipe.reflectHistory[i];
                int remaining = h.points.Count(x => !x.restored);
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    var names = h.points.Select(x => x.avatarName).ToList();
                    string nameText = names.Count <= 3 ? string.Join(T("・"), names) : string.Join(T("・"), names.Take(3)) + T(" ほか {0} 体", names.Count - 3);
                    GUILayout.Label(new GUIContent(T("{0:MM/dd HH:mm}　{1} 体に反映（{2}）", h.Time, h.points.Count, nameText),
                        string.Join("\n", names)), EditorStyles.wordWrappedMiniLabel);
                    GUILayout.FlexibleSpace();
                    if (remaining == 0)
                    {
                        GUILayout.Label(T("取り消し済み"), EditorStyles.miniLabel, GUILayout.Width(130));
                    }
                    else if (GUILayout.Button(new GUIContent(remaining < h.points.Count ? T("残り {0} 体を取り消す", remaining) : T("この反映を取り消す"),
                                 T("この回に反映したアバターを、反映前の状態に戻します")), EditorStyles.miniButton, GUILayout.Width(130)))
                    {
                        undo = h;
                    }
                }
            }

            if (undo != null)
            {
                UndoReflect(undo);
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>その回の反映を取り消す（まだ戻していない、今もこのファイルを使っているアバターだけ）</summary>
        private void UndoReflect(ReflectHistoryEntry h)
        {
            var recipe = Recipe;
            var file = AssignedMesh;
            var targets = new List<(RestorePoint point, SkinnedMeshRenderer renderer)>();
            var skipped = new List<string>();
            foreach (var point in h.points.Where(x => !x.restored))
            {
                var r = ResolveRenderer(point.rendererId);
                if (r == null) skipped.Add(T("・{0}（シーンに見つかりません）", point.avatarName));
                else if (r.sharedMesh != file) skipped.Add(T("・{0}（すでに別のファイルを使っています）", point.avatarName));
                else targets.Add((point, r));
            }

            if (targets.Count == 0)
            {
                EditorUtility.DisplayDialog(Title, T("取り消せるアバターがありません。\n\n") + string.Join("\n", skipped), "OK");
                return;
            }

            string message = T("{0:MM/dd HH:mm} の反映を取り消し、次のアバターを反映前の状態に戻します。\n\n", h.Time) +
                             string.Join("\n", targets.Select(t => T("・{0}　→ {1}", t.point.avatarName, FaceFileLabel(t.point.mesh))));
            if (skipped.Count > 0) message += T("\n\n次のアバターは対象外です：\n") + string.Join("\n", skipped);
            if (!EditorUtility.DisplayDialog(Title, message, T("取り消す"), T("キャンセル"))) return;

            Preview.Restore();
            var warnings = new List<string>();
            Undo.SetCurrentGroupName(Title + T(" (反映の取り消し)"));
            int group = Undo.GetCurrentGroup();
            Undo.RecordObject(recipe, Title + T(" (反映の取り消し)"));
            var fallback = new List<SkinnedMeshRenderer>();
            foreach (var (point, r) in targets)
                if (!RestoreFromPoint(r, point, recipe, warnings))
                    fallback.Add(r);
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
            Undo.CollapseUndoOperations(group);
            if (fallback.Count > 0) RevertRenderers(fallback); // 記録したファイルが無いものは作る前の顔へ
            foreach (var w in warnings) Debug.LogWarning($"[{Title}] {w}");
            Debug.Log(T("[{0}] {1} 体の反映を取り消しました。", Title, targets.Count));
            InvalidateRecipe();
            InvalidateDuplicates();
            _synced = false;
            _dirty = true;
        }

        private void DrawAvatarRow(AvatarEntry e)
        {
            using (var row = new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Space(20); // チェックボックスの場所（描画は最後に行の高さに合わせて行う）

                using (new EditorGUILayout.VerticalScope())
                {
                    string name = e.avatar.name + (e.isSelf ? T("（編集中）") : "") +
                                  (e.avatar.activeInHierarchy ? "" : T("（非表示）"));
                    if (GUILayout.Button(new GUIContent(name, T("クリックで Hierarchy で選択")), RowTitle, GUILayout.MinWidth(100)))
                    {
                        Selection.activeGameObject = e.renderer != null ? e.renderer.gameObject : e.avatar;
                        EditorGUIUtility.PingObject(Selection.activeGameObject);
                    }

                    string detail;
                    if (e.renderer == null) detail = e.reason;
                    else
                    {
                        var used = e.renderer.sharedMesh;
                        string compare = e.state == ReflectState.Applied ? T("反映するファイルと同じ")
                            : e.state == ReflectState.SameFile ? T("元のファイルと同じ")
                            : e.state == ReflectState.DifferentFile ? T("元のファイルと異なる")
                            : e.reason;
                        detail = $"{e.renderer.name}：{FaceFileLabel(used)}（{compare}）";
                    }

                    var detailStyle = new GUIStyle(EditorStyles.miniLabel);
                    if (e.state == ReflectState.DifferentFile) detailStyle.normal.textColor = new Color(0.95f, 0.75f, 0.3f);
                    GUILayout.Label(new GUIContent(detail,
                        e.renderer != null ? AssetDatabase.GetAssetPath(e.renderer.sharedMesh) : ""), detailStyle);

                    if (e.state == ReflectState.Applied)
                        GUILayout.Label(new GUIContent(T("戻すと：{0}", RevertDestinationLabel(e)),
                            T("「反映前に戻す」を押したときに戻る先です")), EditorStyles.miniLabel);

                    if (e.eyelid != EyelidState.NotUsed)
                    {
                        string eyeText, eyeTip;
                        Color eyeColor;
                        switch (e.eyelid)
                        {
                            case EyelidState.Ok:
                                eyeText = T("瞬き・視線：✓ 正しい");
                                eyeTip = T("Avatar Descriptor の瞬き・視線は今の並びに合っています");
                                eyeColor = EditorStyles.miniLabel.normal.textColor;
                                break;
                            case EyelidState.Shifted:
                                eyeText = T("瞬き・視線：⚠ ずれています（並び替え前の設定のまま）");
                                eyeTip = T("Avatar Descriptor はシェイプを番号で参照しているため、並び替えでずれています。チェックして「瞬き・視線を付け直す」を押してください");
                                eyeColor = new Color(1f, 0.55f, 0.2f);
                                break;
                            default:
                                eyeText = T("瞬き・視線：？ 確認できません");
                                eyeTip = T("並び替えたときにこのアバターを開いていなかったため、ずれているか判断できません。Avatar Descriptor の瞬き・視線を確認してください");
                                eyeColor = new Color(0.8f, 0.8f, 0.5f);
                                break;
                        }

                        var eyeStyle = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = eyeColor } };
                        GUILayout.Label(new GUIContent(eyeText, eyeTip), eyeStyle);
                    }
                }

                string status;
                Color color;
                switch (e.state)
                {
                    case ReflectState.Applied:
                        status = T("✓ 反映済み");
                        color = new Color(0.35f, 0.8f, 0.4f);
                        break;
                    case ReflectState.Unavailable:
                        status = T("反映できません");
                        color = new Color(0.65f, 0.65f, 0.65f);
                        break;
                    default:
                        status = T("未反映");
                        color = new Color(1f, 0.6f, 0.2f);
                        break;
                }

                GUILayout.Space(104); // 状態の表示の場所

                // チェックボックスと状態は、行の実際の高さの上下中央に置く
                // （FlexibleSpace で中央寄せすると、スクロールの中では行が画面の高さまで伸びてしまうため）
                var r = row.rect;
                bool selectable = e.state != ReflectState.Unavailable;
                using (new EditorGUI.DisabledScope(!selectable))
                {
                    bool on = selectable && _checked.Contains(e.avatar);
                    bool next = EditorGUI.Toggle(new Rect(r.x + 5f, r.center.y - 8f, 16f, 16f), on);
                    if (next != on)
                    {
                        if (next) _checked.Add(e.avatar);
                        else _checked.Remove(e.avatar);
                    }
                }

                var style = new GUIStyle(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleRight,
                    normal = { textColor = color },
                };
                GUI.Label(new Rect(r.xMax - 106f, r.center.y - 10f, 100f, 20f), new GUIContent(status, e.reason ?? ""), style);
            }
        }

        /// <summary>シーン上の SkinnedMeshRenderer か（Project 内のプレハブ等は対象外）</summary>
        private static bool IsSceneRenderer(SkinnedMeshRenderer r) =>
            r != null && !EditorUtility.IsPersistent(r) && r.gameObject.scene.IsValid();

        private void TryLoad(SkinnedMeshRenderer r)
        {
            if (r != null && !IsSceneRenderer(r))
            {
                EditorUtility.DisplayDialog(Title, T("シーン上のオブジェクトを指定してください（Project 内のプレハブは編集できません）。"), "OK");
                return;
            }

            Load(r);
        }

        // ------------------------------------------------------------------ preview

        /// <summary>
        /// プレビュー。割り当て中のメッシュを複製し、作成中のシェイプを 1 つのブレンドシェイプとして末尾に追加して表示する。
        /// 頂点は書き換えない（Unity のブレンドシェイプ計算と食い違わないようにするため）。
        /// 作成中と同じ名前の作成済みシェイプは、プレビュー中だけ 0 にして二重にかからないようにする。
        /// </summary>
        private void UpdatePreview()
        {
            var assigned = AssignedMesh;
            if (tab != Tab.Create || targetRenderer == null || baseMesh == null || !previewEnabled || _baseMissing ||
                draft.kind == ShapeKind.Separator ||
                EditorApplication.isPlayingOrWillChangePlaymode || !ShapeBakeCore.IsValid(draft, baseMesh) ||
                assigned == null || assigned.vertexCount != baseMesh.vertexCount || DraftConflict() != null)
            {
                Preview.Restore();
                return;
            }

            int n = baseMesh.vertexCount;
            if (_dv == null || _dv.Length != n)
            {
                _dv = new Vector3[n];
                _dn = new Vector3[n];
                _dt = new Vector3[n];
            }
            else
            {
                Array.Clear(_dv, 0, n);
                Array.Clear(_dn, 0, n);
                Array.Clear(_dt, 0, n);
            }

            // 新シェイプ 100 のときの形を 1 フレームで持ち、プレビュー値はウェイトで動かす
            foreach (var s in draft.sources)
            {
                var frames = Frames(s.shapeName);
                if (frames != null) ShapeBakeCore.Accumulate(frames, s.value, _dv, _dn, _dt);
            }

            // 左右分割: 片側だけ残す
            bool? left = null;
            if (draft.split == SideSplit.LeftOnly) left = true;
            if (draft.split == SideSplit.RightOnly) left = false;
            if (left.HasValue)
            {
                var mask = Mask();
                for (int i = 0; i < n; i++)
                {
                    float m = left.Value ? mask[i] : 1f - mask[i];
                    _dv[i] *= m;
                    _dn[i] *= m;
                    _dt[i] *= m;
                }
            }

            // 作成済みの同名シェイプだけを一時的に 0 にする（未作成の既存シェイプは元の表情の一部なので触らない）
            var savedOutputs = new HashSet<string>(SavedShapes.SelectMany(d => ShapeBakeCore.OutputNames(Key(d), d.split)));
            try
            {
                Preview.Show(targetRenderer, _dv, _dn, _dt, PreviewWeight(),
                    DraftOutputs().Where(savedOutputs.Contains));
            }
            catch (Exception e)
            {
                Preview.Restore();
                Debug.LogException(e);
                previewEnabled = false;
                Debug.LogError(T("[{0}] プレビューを表示できなかったため、プレビューを OFF にしました。", Title));
            }

            SceneView.RepaintAll();
        }

        private float PreviewWeight() => previewValue;

        private string PreviewLabel() => T("{0}  値 {1:0}", Key(draft), previewValue);

        private enum PreviewState
        {
            Showing, // プレビュー中
            Off, // 「表示」をオフにしている
            Waiting, // 入力が足りない
            Real, // 作成タブ以外など、実際のメッシュを表示中
        }

        private PreviewState CurrentPreviewState()
        {
            if (Preview.IsShowing) return PreviewState.Showing;
            if (tab != Tab.Create) return PreviewState.Real;
            if (!previewEnabled) return PreviewState.Off;
            return ShapeBakeCore.IsValid(draft, baseMesh) ? PreviewState.Real : PreviewState.Waiting;
        }

        private string StatusText(PreviewState state)
        {
            switch (state)
            {
                case PreviewState.Showing: return T("●  プレビュー中　{0}", PreviewLabel());
                case PreviewState.Off: return T("○  プレビュー OFF　実際のメッシュを表示中");
                case PreviewState.Waiting: return T("○  待機中　出力シェイプ名と元にするシェイプを設定してください");
                default: return T("○  実際のメッシュを表示中");
            }
        }

        private static Color StateColor(PreviewState state)
        {
            switch (state)
            {
                case PreviewState.Showing: return new Color(0.95f, 0.45f, 0.05f, 1f);
                case PreviewState.Waiting: return new Color(0.22f, 0.36f, 0.55f, 1f);
                default: return new Color(0.27f, 0.27f, 0.27f, 1f);
            }
        }

        private static readonly Color PreviewColor = StateColor(PreviewState.Showing);

        private static GUIStyle _statusStyle;

        private static GUIStyle StatusStyle => _statusStyle ?? (_statusStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(8, 8, 0, 0),
            normal = { textColor = Color.white },
        });

        /// <summary>プレビューの状態を色付きの帯で表示する（ウィンドウとシーンビューで共通）</summary>
        private void DrawStatusBar(Rect rect, PreviewState state, string suffix = "")
        {
            EditorGUI.DrawRect(rect, StateColor(state));
            var content = new GUIContent(StatusText(state) + suffix,
                state == PreviewState.Showing
                    ? T("表示のために顔のデータを一時的に差し替えています。保存・ウィンドウを閉じる・シーン保存・再生で自動的に元に戻ります")
                    : "");
            GUI.Label(rect, content, StatusStyle);
        }

        /// <summary>元メッシュのシェイプデータ（初回だけ読み込む）</summary>
        private ShapeBakeCore.Frame[] Frames(string shapeName)
        {
            if (string.IsNullOrEmpty(shapeName)) return null;
            int idx = baseMesh.GetBlendShapeIndex(shapeName);
            if (idx < 0) return null;
            if (_frameCacheMesh != baseMesh)
            {
                _frameCache.Clear();
                _frameCacheMesh = baseMesh;
            }

            if (!_frameCache.TryGetValue(idx, out var f)) _frameCache[idx] = f = ShapeBakeCore.ReadFrames(baseMesh, idx);
            return f;
        }

        private float[] Mask()
        {
            var key = (baseMesh, targetRenderer.transform, draft.splitBlendWidth);
            if (_maskCache == null || _maskKey != key)
            {
                _maskCache = ShapeBakeCore.LeftMask(baseMesh, targetRenderer.transform,
                    ShapeBakeCore.FindAvatarRoot(targetRenderer.transform), draft.splitBlendWidth);
                _maskKey = key;
            }

            return _maskCache;
        }

        /// <summary>作成中のシェイプの出力名が、他の作成済みシェイプの出力名と重複していればその説明</summary>
        private string DraftConflict()
        {
            if (draft.kind == ShapeKind.Separator && !string.IsNullOrWhiteSpace(draft.outputName) &&
                baseMesh != null && baseMesh.GetBlendShapeIndex(Key(draft)) >= 0)
                return T("「{0}」は既存のシェイプと同じ名前です。UI用シェイプには別の名前を付けてください。", Key(draft));
            var mine = new HashSet<string>(DraftOutputs());
            foreach (var d in SavedShapes)
            {
                if (Key(d) == Key(draft)) continue;
                foreach (var n in ShapeBakeCore.OutputNames(Key(d), d.split))
                    if (mine.Contains(n))
                        return T("「{0}」は作成済みの「{1}」と名前が重複しています。出力シェイプ名を変えてください。", n, Key(d));
            }

            return null;
        }

        /// <summary>作成済み＋作成中のシェイプで、置き換えになる元シェイプと退避先の名前</summary>
        private Dictionary<string, string> BackupNamesWithDraft()
        {
            var outputs = ShapesWithDraft().Where(d => ShapeBakeCore.IsValid(d, baseMesh) || Key(d) == Key(draft))
                .SelectMany(d => ShapeBakeCore.OutputNames(Key(d), d.split)).Where(n => n.Length > 0);
            return ShapeBakeCore.PlanBackups(baseMesh, outputs, Suffix).ToDictionary(x => x.original, x => x.backup);
        }

        /// <summary>レシピのうち、元メッシュに見つからないシェイプを参照しているもの</summary>
        private List<string> MissingSources(ShapeDefinition d) =>
            d.sources.Where(x => !string.IsNullOrEmpty(x.shapeName) && baseMesh.GetBlendShapeIndex(x.shapeName) < 0)
                .Select(x => x.shapeName).ToList();

        private List<string> DraftOutputs() =>
            string.IsNullOrWhiteSpace(draft.outputName)
                ? new List<string>()
                : ShapeBakeCore.OutputNames(draft).ToList();

        /// <summary>
        /// 対象メッシュ上の現在値。元のシェイプが退避されている名前は退避先の値を読む
        /// （元にするシェイプは常に元メッシュのデータなので、対応する値も元のシェイプの値）。
        /// </summary>
        private float CurrentWeight(string name)
        {
            var recipe = Recipe;
            if (recipe != null)
                foreach (var (original, backup) in BackupPlan(recipe))
                    if (original == name)
                    {
                        name = backup;
                        break;
                    }

            var muted = Preview.MutedWeight(targetRenderer, name);
            if (muted.HasValue) return muted.Value;
            var m = AssignedMesh;
            int i = m != null ? m.GetBlendShapeIndex(name) : -1;
            var cur = targetRenderer.sharedMesh;
            return i >= 0 && cur != null && i < cur.blendShapeCount ? targetRenderer.GetBlendShapeWeight(i) : 0f;
        }

        private ShapeRecipe _backupPlanFor;
        private List<(string original, string backup)> _backupPlan;

        private List<(string original, string backup)> BackupPlan(ShapeRecipe recipe)
        {
            if (_backupPlanFor != recipe || _backupPlan == null)
            {
                _backupPlan = ShapeBakeCore.PlanBackups(recipe);
                _backupPlanFor = recipe;
            }

            return _backupPlan;
        }

        // ------------------------------------------------------------------ GUI

        private void OnGUI()
        {
            if (Event.current.type == EventType.MouseMove) Repaint(); // 一覧の行のマウスオーバー表示用
            // 起動直後はアイコンの読み込みが間に合わないことがあるので、取れた時点で設定する
            if (titleContent.image == null && Icon != null) titleContent = new GUIContent(Title, Icon);
            EditorGUIUtility.labelWidth = 130;

            // ライセンスが無いコンピュータでは、ウィンドウ全体を使えないようにする
            if (!BlendshapeEditorLicense.IsLicensed())
            {
                DrawLicenseRequired();
                DrawFooter();
                return;
            }

            SyncBase();

            // 対象メッシュと使用中のファイルは全タブ共通なので、タブの上に固定で表示する
            DrawTarget();

            // 対象メッシュが未指定のときは、タブの代わりにアイコンと案内を表示する
            if (targetRenderer == null)
            {
                DrawEmptyState();
                DrawFooter();
                return;
            }

            EditorGUILayout.Space(4);
            if ((int)tab > (int)Tab.Manage) tab = Tab.Manage; // 以前の 4 タブ構成からの移行
            var newTab = (Tab)GUILayout.Toolbar((int)tab, new[] { T("作成"), T("管理") }, GUILayout.Height(24));
            if (newTab != tab)
            {
                tab = newTab;
                suffixEdit = Suffix;
                _dirty = true;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (targetRenderer != null && baseMesh != null && baseMesh.blendShapeCount > 0 && !_baseMissing)
            {
                if (tab == Tab.Create)
                {
                    DrawCreateTab();
                }
                else
                {
                    EditorGUILayout.Space(6);
                    // 表示順: 作成シェイプ一覧 / 並び順 / アバター管理 / 設定
                    var subOrder = new[] { ManageTab.Shapes, ManageTab.Order, ManageTab.Avatars, ManageTab.Settings };
                    int cur = Array.IndexOf(subOrder, manageTab);
                    int next = GUILayout.Toolbar(Mathf.Max(0, cur), new[] { T("作成シェイプ一覧"), T("並び順"), T("アバター管理"), T("設定") },
                        EditorStyles.miniButton, GUILayout.Height(20));
                    if (subOrder[next] != manageTab)
                    {
                        manageTab = subOrder[next];
                        suffixEdit = Suffix;
                        headerEdit = Recipe != null ? Recipe.backupHeader : ShapeRecipe.DefaultBackupHeader;
                    }

                    switch (manageTab)
                    {
                        case ManageTab.Shapes: DrawShapesTab(); break;
                        case ManageTab.Order: DrawOrderTab(); break;
                        case ManageTab.Avatars: DrawAvatarsTab(); break;
                        default: DrawSettingsTab(); break;
                    }
                }
            }

            EditorGUILayout.EndScrollView();
            DrawFooter();
        }

        // ------------------------------------------------------------------ icon / empty state

        private static Texture2D _icon;

        /// <summary>ツールのアイコン（このスクリプトと同じフォルダの Icons/BlendshapeEditorIcon.png）</summary>
        private static Texture2D Icon
        {
            get
            {
                if (_icon != null) return _icon;
                var guids = AssetDatabase.FindAssets("BlendshapeEditorIcon t:Texture2D");
                var path = guids.Select(AssetDatabase.GUIDToAssetPath)
                    .FirstOrDefault(x => x.EndsWith("/Editor/Icons/BlendshapeEditorIcon.png"));
                if (path != null) _icon = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                return _icon;
            }
        }

        private static GUIStyle _footerStyle;

        /// <summary>ウィンドウ下の表示言語と、右下のツール名とバージョン（他の Azipa Tools と同じ表示）</summary>
        private static void DrawFooter()
        {
            if (_footerStyle == null)
                _footerStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { alignment = TextAnchor.MiddleRight };
            using (new EditorGUILayout.HorizontalScope())
            {
                // 表示言語（ライセンスの画面や空の状態でも切り替えられるよう、常に表示する）
                EditorGUI.BeginChangeCheck();
                int lang = EditorGUILayout.Popup((int)Language, LanguageLabels, EditorStyles.miniPullDown, GUILayout.Width(90f));
                if (EditorGUI.EndChangeCheck()) Language = (BseLanguage)lang;
                EditorGUILayout.LabelField($"{Title}  v{Version}", _footerStyle, GUILayout.ExpandWidth(true), GUILayout.Height(14f));
            }
        }

        private static GUIStyle _emptyTitleStyle, _emptyBodyStyle;

        private static GUIStyle EmptyTitleStyle => _emptyTitleStyle ?? (_emptyTitleStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.UpperCenter,
            fontSize = 13,
            wordWrap = true,
        });

        private static GUIStyle EmptyBodyStyle => _emptyBodyStyle ?? (_emptyBodyStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
        {
            alignment = TextAnchor.UpperCenter,
        });

        /// <summary>未購入（ライセンスが無い）ときの画面</summary>
        private void DrawLicenseRequired()
        {
            GUILayout.FlexibleSpace();
            if (Icon != null)
            {
                float size = Mathf.Min(128f, position.width - 32f);
                var rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(true));
                GUI.DrawTexture(new Rect(rect.x + (rect.width - size) / 2f, rect.y, size, size), Icon, ScaleMode.ScaleToFit, true);
            }

            EditorGUILayout.Space(10);
            GUILayout.Label(T("Blendshape Editor のライセンスが見つかりません"), EmptyTitleStyle);
            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(16);
                EditorGUILayout.HelpBox(
                    T("このコンピュータには Blendshape Editor の使用が許諾されていません。\n") +
                    T("Booth で Blendshape Editor を購入し、ダウンロードした ZIP に入っている「{0}」を", BlendshapeEditorLicense.InstallerFileName) +
                    T("Unity にインポートしてください（この作業は 1 台のコンピュータにつき 1 回だけ必要です）。\n\n") +
                    T("購入済みなのにこの画面が表示される場合は、Booth から最新の ZIP をダウンロードして、") +
                    T("ライセンスインストーラーをインポートし直してください。\n\n") +
                    T("作成済みのシェイプ（保存ファイル）は、ライセンスが無くてもアバターでそのまま使えます。"),
                    MessageType.Warning);
                GUILayout.Space(16);
            }

            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                bool hasUrl = !string.IsNullOrEmpty(BlendshapeEditorLicense.BoothUrl);
                using (new EditorGUI.DisabledScope(!hasUrl))
                    if (GUILayout.Button(hasUrl ? T("Booth で購入する") : T("Booth（準備中）"), GUILayout.Width(160), GUILayout.Height(28)))
                        Application.OpenURL(BlendshapeEditorLicense.BoothUrl);
                if (GUILayout.Button(new GUIContent(T("ライセンスを再確認"), T("ライセンスをインストールした直後に押してください")),
                        GUILayout.Width(140), GUILayout.Height(28)))
                    BlendshapeEditorLicense.IsLicensed(true);
                GUILayout.FlexibleSpace();
            }

            GUILayout.FlexibleSpace();
        }

        private void OnFocus()
        {
            // ライセンスインストーラーをインポートした直後などに、ウィンドウに戻ったら確認し直す
            BlendshapeEditorLicense.IsLicensed(true);
        }

        /// <summary>対象メッシュが未指定のとき、ウィンドウの余白にアイコンと案内を表示する</summary>
        private void DrawEmptyState()
        {
            var area = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (Event.current.type != EventType.Repaint) return;

            const float titleH = 22f, bodyH = 36f, gap = 10f;
            float size = Mathf.Min(160f, area.width - 32f, area.height - titleH - bodyH - gap * 3);
            float total = Mathf.Max(0, size) + gap + titleH + bodyH;
            float y = area.y + Mathf.Max(8f, (area.height - total) / 2f);

            if (Icon != null && size >= 48f)
            {
                GUI.DrawTexture(new Rect(area.x + (area.width - size) / 2f, y, size, size), Icon, ScaleMode.ScaleToFit, true);
                y += size + gap;
            }

            GUI.Label(new Rect(area.x + 16, y, area.width - 32, titleH), T("対象メッシュを指定してください"), EmptyTitleStyle);
            y += titleH;
            GUI.Label(new Rect(area.x + 16, y, area.width - 32, bodyH),
                T("シェイプキーを持つ顔の SkinnedMeshRenderer を、上の欄にドラッグ＆ドロップするか、\n") +
                T("Hierarchy で選んで「選択中」を押してください。"), EmptyBodyStyle);
        }

        private static void Header(string text)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(text, EditorStyles.boldLabel);
        }

        /// <summary>タブの冒頭に出す 1 行の説明</summary>
        private static void TabIntro(string text)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(text, EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawTarget()
        {
            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                var r = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(T("対象メッシュ"), targetRenderer,
                    typeof(SkinnedMeshRenderer), true);
                if (r != targetRenderer) TryLoad(r);

                var sel = Selection.activeGameObject != null
                    ? Selection.activeGameObject.GetComponent<SkinnedMeshRenderer>()
                    : null;
                using (new EditorGUI.DisabledScope(!IsSceneRenderer(sel)))
                    if (GUILayout.Button(T("選択中"), GUILayout.Width(52))) TryLoad(sel);
            }

            if (targetRenderer == null)
            {
                // 案内は下の余白（DrawEmptyState）に表示する
            }
            else if (_baseMissing)
                EditorGUILayout.HelpBox(
                    T("シェイプを作る前の顔のデータ（FBX など）が見つかりません。削除・移動していないか確認してください。\n") +
                    T("このデータが無いとシェイプを正しく作り直せないため、編集できません。"), MessageType.Error);
            else if (baseMesh == null || baseMesh.blendShapeCount == 0)
                EditorGUILayout.HelpBox(T("このメッシュにはシェイプキーがありません。"), MessageType.Warning);
            else
            {
                // 使用中のファイルを 1 行で
                var assigned = AssignedMesh;
                var recipe = Recipe;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(T("使用中のファイル"), GUILayout.Width(130));
                    EditorGUILayout.LabelField(new GUIContent(
                            FaceFileLabel(assigned) + (recipe != null
                                ? T("（作ったシェイプ {0} 個）", recipe.shapes.Count)
                                : T("（まだシェイプを作っていません）")),
                            AssetDatabase.GetAssetPath(assigned)),
                        EditorStyles.miniLabel);
                }

                DrawDuplicateNotice();
            }

            EditorGUILayout.Space(2);
            var line = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(line, new Color(0.5f, 0.5f, 0.5f, 0.35f));
        }

        // ------------------------------------------------------------------ create tab

        private void DrawCreateTab()
        {
            EditorGUI.BeginChangeCheck();
            DrawDraft();
            if (EditorGUI.EndChangeCheck()) _dirty = true;

            if (draft.kind == ShapeKind.Normal) DrawPreview();
            DrawCreateButton();
        }

        /// <summary>今のシェイプキーの並び（保存済みのファイルの並び、まだ作っていなければ元の顔のデータの並び）</summary>
        private List<string> CurrentOrderNames() => ShapeNames(AssignedMesh).ToList();

        /// <summary>作成中のシェイプを入れる位置を反映した並び。既定の位置なら null（ツールが決める）</summary>
        private List<string> DraftOrder()
        {
            var saved = Recipe != null && Recipe.order.Count > 0 ? Recipe.order.ToList() : null;
            if (string.IsNullOrEmpty(insertAfter)) return saved;

            var list = saved ?? CurrentOrderNames();
            var outs = DraftOutputs();
            list.RemoveAll(outs.Contains);
            int at = insertAfter == TopToken ? 0 : list.IndexOf(insertAfter) + 1;
            if (insertAfter != TopToken && at <= 0) at = list.Count;
            list.InsertRange(at, outs);
            return list;
        }

        private string InsertLabel()
        {
            if (insertAfter == TopToken) return T("先頭");
            if (!string.IsNullOrEmpty(insertAfter)) return T("「{0}」の下", insertAfter);
            bool exists = DraftOutputs().Any(n => CurrentOrderNames().Contains(n));
            return exists ? T("変更しない（今の位置）") : T("末尾（退避用の見出しの上）");
        }

        private void DrawInsertPosition()
        {
            Header(T("追加する位置"));
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent(T("位置"), T("完成したシェイプキー一覧の中で、どこに入れるかを選びます")));
                var label = new GUIContent(InsertLabel());
                var rect = GUILayoutUtility.GetRect(label, EditorStyles.popup);
                if (GUI.Button(rect, label, EditorStyles.popup))
                {
                    var outs = new HashSet<string>(DraftOutputs());
                    var items = new List<(string name, string label)>
                    {
                        ("", T("既定の位置（新規は末尾、既存は今の位置）")),
                        (TopToken, T("先頭")),
                    };
                    items.AddRange(CurrentOrderNames().Where(n => !outs.Contains(n)).Select(n => (n, T("{0} の下", n))));
                    new ShapeNameDropdown(items, n =>
                    {
                        Undo.RecordObject(this, Title);
                        insertAfter = n;
                        Repaint();
                    }, T("シェイプキーがありません")).Show(rect);
                }
            }

            if (!string.IsNullOrEmpty(insertAfter))
                EditorGUILayout.LabelField(
                    T("途中に入れると、後ろのシェイプの番号がずれます。編集中のアバターの瞬き・視線は自動で付け直します") +
                    T("（他のアバターは「管理 › アバター管理」で付け直せます）。"),
                    EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawDraft()
        {
            // 親タブ（作成 / 管理）と見分けやすいよう、子タブの上に見出しを付ける
            Header(T("シェイプの種類"));
            var kind = (ShapeKind)GUILayout.Toolbar((int)draft.kind, new[]
            {
                new GUIContent(T("シェイプ作成・上書き編集"), T("元にするシェイプを組み合わせて、新しいシェイプを作ったり既存のシェイプを上書きしたりします")),
                new GUIContent(T("UI用シェイプ作成"), T("シェイプキー一覧を見やすく区切るための空のシェイプです（例: ---- VRCHAT ----）。顔は変形しません")),
            }, GUILayout.Height(22));
            if (kind != draft.kind)
            {
                draft.kind = kind;
                draft.split = SideSplit.None;
            }

            // 種類の切り替えの下に少し余白を取り、その下にクリアを置く
            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent(T("クリア"), T("入力内容を消して新しく作り始めます")),
                        EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    draft = new ShapeDefinition { kind = draft.kind };
                    insertAfter = "";
                    GUI.changed = true;
                }
            }

            if (draft.kind == ShapeKind.Separator)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    draft.outputName = EditorGUILayout.TextField(new GUIContent(T("UI用シェイプの名前"), T("例: ---- 表情 ----")),
                        draft.outputName);
                    if (GUILayout.Button(new GUIContent(T("整形"), T("「---- 名前 ----」の形に整えます")), EditorStyles.miniButton,
                            GUILayout.Width(40)))
                    {
                        var core = draft.outputName.Trim().Trim('-', '=', ' ', '　');
                        draft.outputName = $"---- {(core.Length > 0 ? core : T("区切り"))} ----";
                        GUI.changed = true;
                    }
                }

                EditorGUILayout.LabelField(T("UI用シェイプは顔を変形しない空のシェイプです。シェイプキー一覧を見やすく区切るために使います。"),
                    EditorStyles.wordWrappedMiniLabel);
                var sepConflict = DraftConflict();
                if (sepConflict != null) EditorGUILayout.HelpBox(sepConflict, MessageType.Error);
                else if (SavedWithSameName != null)
                    EditorGUILayout.HelpBox(T("作成済みの「{0}」を上書きします。", Key(draft)), MessageType.None);
                DrawInsertPosition();
                return;
            }

            editExisting = EditorGUILayout.ToggleLeft(new GUIContent(T("既存シェイプを編集する"),
                T("選んだシェイプと同じ名前で出力し、元にするシェイプにもそのシェイプを設定します。\n") +
                T("元のシェイプは退避名で残ります")), editExisting);

            if (editExisting)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel(T("編集するシェイプ"));
                    var label = new GUIContent(string.IsNullOrWhiteSpace(draft.outputName) ? T("(シェイプを選択)") : Key(draft));
                    var rect = GUILayoutUtility.GetRect(label, EditorStyles.popup);
                    if (GUI.Button(rect, label, EditorStyles.popup))
                        new ShapeNameDropdown(EditableShapes(), n =>
                        {
                            Undo.RecordObject(this, Title);
                            SelectExisting(n);
                            OnPickerChange();
                        }, T("シェイプキーがありません")).Show(rect);
                }

                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.TextField(T("出力シェイプ名"), draft.outputName);
            }
            else
            {
                draft.outputName = EditorGUILayout.TextField(T("出力シェイプ名"), draft.outputName);
            }
            var existing = ShapeBakeCore.OutputNames(Key(draft), draft.split)
                .Where(n => n.Length > 0 && baseMesh.GetBlendShapeIndex(n) >= 0).ToList();
            if (existing.Count > 0)
            {
                var plan = BackupNamesWithDraft();
                EditorGUILayout.HelpBox(
                    T("既存の「{0}」と同じ名前・同じ位置に新しいシェイプを入れます。\n", string.Join("」「", existing)) +
                    T("元のシェイプは「{0}」", string.Join("」「", existing.Select(n => plan.TryGetValue(n, out var b) ? b : n + Suffix))) +
                    T("として「{0}」の下に残します。", ShapeBakeCore.NormalizeHeader(Recipe != null ? Recipe.backupHeader : null)),
                    MessageType.Info);
            }

            var conflict = DraftConflict();
            if (conflict != null) EditorGUILayout.HelpBox(conflict, MessageType.Error);
            else if (SavedWithSameName != null)
                EditorGUILayout.HelpBox(T("作成済みの「{0}」を上書きします。", Key(draft)), MessageType.None);

            Header(T("元にするシェイプ（新シェイプ 100 のときの値）"));
            nonZeroOnly = EditorGUILayout.ToggleLeft(
                new GUIContent(T("{0} で 0 以外の値が入っているシェイプだけから選ぶ", targetRenderer.name),
                    T("シェイプ選択リストを、今のメッシュ上で値が設定されているものに絞り込みます")), nonZeroOnly);

            using (new EditorGUILayout.HorizontalScope())
            {
                overLimit = EditorGUILayout.ToggleLeft(new GUIContent(T("限界突破"),
                    T("値を ±100 を超えて設定できるようにします。100 を超えた分は同じ方向に直線的に伸びます")), overLimit,
                    GUILayout.Width(80));
                using (new EditorGUI.DisabledScope(!overLimit))
                {
                    EditorGUIUtility.labelWidth = 40;
                    overLimitMax = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent(T("上限 ±"), "100〜1000"),
                        overLimitMax, GUILayout.Width(100)), 100, 1000);
                    EditorGUIUtility.labelWidth = 130;
                }
            }

            if (overLimit)
                EditorGUILayout.HelpBox(T("±100 を超える値は形が破綻しやすいので、プレビューで確認しながら調整してください。"), MessageType.None);

            int remove = -1;
            for (int i = 0; i < draft.sources.Count; i++)
            {
                var s = draft.sources[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool missing = !string.IsNullOrEmpty(s.shapeName) && baseMesh.GetBlendShapeIndex(s.shapeName) < 0;
                    var c = GUI.color;
                    if (missing) GUI.color = new Color(1f, 0.6f, 0.6f);
                    ShapePicker(s.shapeName, T("(シェイプを選択)"), n =>
                    {
                        Undo.RecordObject(this, Title);
                        s.shapeName = n;
                        if (nonZeroOnly) s.value = CancelValue(n);
                        OnPickerChange();
                    }, EditorStyles.popup, GUILayout.Width(Mathf.Max(140, position.width * 0.38f)));
                    GUI.color = c;

                    // 限界突破オフでも、既に範囲外の値が入っている場合は値を勝手に丸めない
                    float range = Mathf.Max(overLimit ? overLimitMax : 100f, Mathf.Ceil(Mathf.Abs(s.value)));
                    s.value = EditorGUILayout.Slider(s.value, -range, range);

                    float cur = missing || string.IsNullOrEmpty(s.shapeName) ? 0f : CurrentWeight(s.shapeName);
                    GUILayout.Label(new GUIContent(T("現在 {0:0.#}", cur), T("{0} 上の現在の値", targetRenderer.name)),
                        EditorStyles.miniLabel, GUILayout.Width(52));
                    using (new EditorGUI.DisabledScope(Mathf.Approximately(cur, 0f)))
                        if (GUILayout.Button(new GUIContent(T("打消"), T("値を −現在値 にして、新シェイプ 100 でこのシェイプを 0 と同じ状態にする")),
                                EditorStyles.miniButton, GUILayout.Width(36)))
                            s.value = -cur;
                    if (GUILayout.Button("×", GUILayout.Width(22))) remove = i;
                }
            }

            if (remove >= 0)
            {
                draft.sources.RemoveAt(remove);
                GUI.changed = true;
            }

            ShapePicker(null, T("＋ シェイプを追加"), n =>
            {
                Undo.RecordObject(this, Title);
                // 「0 以外の値が入っているシェイプだけから選ぶ」で選んだときは、今の値を打ち消す値で始める
                draft.sources.Add(new SourceShape { shapeName = n, value = nonZeroOnly ? CancelValue(n) : 100f });
                if (string.IsNullOrWhiteSpace(draft.outputName)) draft.outputName = n + "_custom";
                OnPickerChange();
            }, EditorStyles.miniButton);

            Header(T("左右分割"));
            draft.split = (SideSplit)EditorGUILayout.Popup(T("分割"), (int)draft.split,
                new[] { T("しない"), T("左側のみ"), T("右側のみ") });
            if (draft.split != SideSplit.None)
            {
                draft.splitBlendWidth = EditorGUILayout.FloatField(new GUIContent(T("境目のぼかし幅 (m)"),
                    T("アバター中心（X=0）を境に、この幅でなめらかに左右を分けます")), Mathf.Max(0f, draft.splitBlendWidth));
                draft.splitBlendWidth = Mathf.Max(0f, draft.splitBlendWidth);
                EditorGUILayout.HelpBox(T("左右はキャラクター本人から見た向きです（アバターが +Z を向いている前提）。"), MessageType.None);
            }

            DrawInsertPosition();
        }

        private void OnPickerChange()
        {
            _dirty = true;
            Repaint();
        }

        private void DrawPreview()
        {
            EditorGUILayout.Space(10);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // 1 行目: 状態の帯 ＋ 表示の ON/OFF
                var row = GUILayoutUtility.GetRect(0, 26, GUILayout.ExpandWidth(true));
                var toggleRect = new Rect(row.xMax - 76, row.y, 76, row.height);
                var barRect = new Rect(row.x, row.y, row.width - 80, row.height);
                DrawStatusBar(barRect, CurrentPreviewState());

                EditorGUI.BeginChangeCheck();
                previewEnabled = GUI.Toggle(toggleRect, previewEnabled,
                    new GUIContent(previewEnabled ? T("表示 ON") : T("表示 OFF"), T("シーンへのプレビュー表示を切り替えます")),
                    "Button");
                if (EditorGUI.EndChangeCheck()) _dirty = true;

                if (!previewEnabled) return;

                // 2 行目以降: プレビューの値
                EditorGUILayout.Space(4);
                using (new EditorGUI.DisabledScope(!ShapeBakeCore.IsValid(draft, baseMesh)))
                {
                    EditorGUI.BeginChangeCheck();
                    previewValue = EditorGUILayout.Slider(T("新シェイプの値"), previewValue, 0f, 100f);
                    if (EditorGUI.EndChangeCheck()) _weightOnly = true;
                }
            }
        }

        private void DrawCreateButton()
        {
            EditorGUILayout.Space(10);
            bool update = SavedWithSameName != null;
            using (new EditorGUI.DisabledScope(!ShapeBakeCore.IsValid(draft, baseMesh) || DraftConflict() != null))
                if (GUILayout.Button(update ? T("「{0}」を更新", Key(draft)) : T("作成"), GUILayout.Height(32)))
                {
                    // 作成・更新が終わったら作成タブを空に戻す（プレビューも止まる）
                    if (Commit(ShapesWithDraft(), Suffix, DraftOrder()))
                    {
                        draft = new ShapeDefinition { kind = draft.kind };
                        insertAfter = "";
                    }
                    GUIUtility.ExitGUI();
                }

            EditorGUILayout.HelpBox(
                Recipe != null
                    ? T("「{0}」に保存します。作成済みのシェイプは「管理」タブの「作成シェイプ一覧」で確認できます。", Path.GetFileName(AssetDatabase.GetAssetPath(AssignedMesh)))
                    : T("初回は保存先を選びます。シェイプを追加した顔のデータを新しいファイル（.asset）として保存し、対象メッシュに適用します。元の FBX は変更しません。"),
                MessageType.None);
        }

        // ------------------------------------------------------------------ manage tab

        /// <summary>シェイプをまだ作っていないときの案内（作成シェイプ一覧・アバター管理タブ共通）</summary>
        private bool DrawNoRecipe()
        {
            if (Recipe != null && Recipe.shapes.Count > 0) return false;
            EditorGUILayout.HelpBox(T("まだ作成したシェイプはありません。「作成」タブで作成してください。"), MessageType.Info);
            if (AssignedMesh == baseMesh)
                EditorGUILayout.HelpBox(
                    T("以前保存したファイル（.asset）がある場合は、対象メッシュのレンダラーにそのファイルを割り当てると読み込めます。"),
                    MessageType.None);
            return true;
        }

        // ------------------------------------------------------------------ shapes tab

        private void DrawShapesTab()
        {
            TabIntro(T("作成したシェイプの一覧です。行をクリックすると詳細を表示します。"));
            if (DrawNoRecipe()) return;
            var recipe = Recipe;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(T("{0} 個", recipe.shapes.Count), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(T("すべて展開"), EditorStyles.miniButtonLeft, GUILayout.Width(70)))
                    foreach (var d in recipe.shapes) _expandedShapes.Add(Key(d));
                if (GUILayout.Button(T("すべてたたむ"), EditorStyles.miniButtonRight, GUILayout.Width(70)))
                    _expandedShapes.Clear();
            }

            var plan = BackupPlan(recipe).ToDictionary(x => x.original, x => x.backup);
            ShapeDefinition toEdit = null, toDelete = null;
            foreach (var d in recipe.shapes)
            {
                var outputs = ShapeBakeCore.OutputNames(d).ToList();
                var replaced = outputs.Where(plan.ContainsKey).ToList();
                var missing = MissingSources(d);
                bool expanded = _expandedShapes.Contains(Key(d));

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    // 1 行目（初期表示）: 名前・置き換え/新規のバッジ・詳細/編集/削除
                    // 高さを揃えるため、行の矩形を自前で割り付けて描く
                    var row = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
                    float right = row.xMax;
                    Rect Take(float width, float gap = 4f)
                    {
                        right -= width;
                        var r = new Rect(right, row.y + 2, width, 18);
                        right -= gap;
                        return r;
                    }

                    var deleteRect = Take(44, 0);
                    var editRect = Take(44, 8);
                    var badgeRect = Take(116, 8);
                    var warnRect = missing.Count > 0 ? Take(18, 4) : Rect.zero;
                    var nameRect = new Rect(row.x + 4, row.y, Mathf.Max(0, right - row.x - 4), row.height);

                    // 名前（クリックで詳細の開閉）
                    // 編集・削除ボタン以外の部分をクリックすると詳細を開閉する（マウスを乗せると薄く強調）
                    var clickRect = new Rect(row.x, row.y, Mathf.Max(0, editRect.x - 6 - row.x), row.height);
                    if (Event.current.type == EventType.Repaint && clickRect.Contains(Event.current.mousePosition))
                        EditorGUI.DrawRect(clickRect, new Color(1f, 1f, 1f, 0.05f));
                    EditorGUIUtility.AddCursorRect(clickRect, MouseCursor.Link);
                    GUI.Label(nameRect, new GUIContent(string.Join(" / ", outputs),
                        expanded ? T("クリックで詳細を閉じる") : T("クリックで詳細を表示")), ShapeNameStyle);
                    bool toggle = Event.current.type == EventType.MouseDown && Event.current.button == 0 &&
                                  clickRect.Contains(Event.current.mousePosition);
                    if (toggle) Event.current.Use();

                    if (missing.Count > 0)
                        GUI.Label(warnRect, new GUIContent("⚠", T("元にするシェイプが見つからないものがあります。詳細を確認してください")),
                            WarnStyle);

                    // 置き換え / 新規 のバッジ
                    bool isReplace = replaced.Count > 0;
                    bool isSeparator = d.kind == ShapeKind.Separator;
                    GUI.DrawTexture(new Rect(badgeRect.x, badgeRect.y + 1, badgeRect.width, badgeRect.height - 2),
                        Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
                        isSeparator ? new Color(0.45f, 0.45f, 0.45f)
                        : isReplace ? new Color(0.85f, 0.45f, 0.1f) : new Color(0.25f, 0.5f, 0.8f), 0, 8f);
                    GUI.Label(badgeRect, new GUIContent(isSeparator ? T("UI用シェイプ") : isReplace ? T("既存シェイプの上書き") : T("新規"),
                        isSeparator ? T("顔を変形しない、一覧を見やすくするための空のシェイプです")
                        : isReplace ? T("既存のシェイプを置き換えています") : T("新しい名前で追加しています")), BadgeStyle);

                    if (GUI.Button(editRect, T("編集"), EditorStyles.miniButtonLeft)) toEdit = d;
                    if (GUI.Button(deleteRect, T("削除"), EditorStyles.miniButtonRight)) toDelete = d;

                    if (toggle)
                    {
                        if (expanded) _expandedShapes.Remove(Key(d));
                        else _expandedShapes.Add(Key(d));
                        Repaint();
                    }

                    if (!expanded) continue;

                    // 詳細（展開時）
                    EditorGUILayout.Space(2);
                    using (new EditorGUILayout.VerticalScope(DetailStyle))
                    {
                        if (d.kind == ShapeKind.Separator)
                        {
                            EditorGUILayout.LabelField(T("顔を変形しない UI 用のシェイプです。"), EditorStyles.miniLabel);
                            continue;
                        }

                        EditorGUILayout.LabelField(T("元にするシェイプ"), EditorStyles.miniBoldLabel);
                        foreach (var src in d.sources)
                            EditorGUILayout.LabelField(T("・{0}　{1:0.#}", src.shapeName, src.value), EditorStyles.miniLabel);

                        if (replaced.Count > 0)
                            EditorGUILayout.LabelField(
                                T("元のシェイプは {0} に退避しています", string.Join("、", replaced.Select(n => plan[n]))),
                                EditorStyles.miniLabel);
                        if (d.split == SideSplit.LeftOnly) EditorGUILayout.LabelField(T("左側のみ"), EditorStyles.miniLabel);
                        if (d.split == SideSplit.RightOnly) EditorGUILayout.LabelField(T("右側のみ"), EditorStyles.miniLabel);

                        if (missing.Count > 0)
                            EditorGUILayout.HelpBox(
                                T("アバターの顔のデータに見つからないシェイプがあります: {0}\n", string.Join("、", missing)) +
                                (ShapeBakeCore.IsValid(d, baseMesh)
                                    ? T("見つからないシェイプは無視して作成されます。")
                                    : T("このシェイプは作成されません（設定は残っています）。「編集」で元にするシェイプを選び直してください。")),
                                MessageType.Warning);
                    }
                }
            }

            if (toEdit != null)
            {
                LoadDraft(toEdit);
                tab = Tab.Create;
                _dirty = true;
                GUIUtility.ExitGUI();
            }

            if (toDelete != null)
            {
                DeleteShape(toDelete);
                GUIUtility.ExitGUI();
            }

        }

        // ------------------------------------------------------------------ order tab
        //
        // 並び順は保存ボタンで反映する。編集中の並び（orderDraft）はウィンドウに保存し、Unity の Undo に記録するので
        // Ctrl+Z / Ctrl+Y で取り消し・やり直しができる。「変更前と比較」で保存済みの並びと左右に並べて見比べられる。

        [SerializeField] private List<string> orderDraft; // 編集中の並び（保存するまで反映しない）
        [SerializeField] private Mesh orderDraftFor; // どのファイルの並びを編集しているか
        [SerializeField] private int orderEditSerial; // 編集のたびに増える（Undo で戻ると減る）
        [SerializeField] private bool orderCompare; // 変更前と比較（左右に分割表示）
        [SerializeField] private bool orderChangedOnly; // 比較時に移動したシェイプだけ表示

        private int _orderBaseSerial; // 編集を始めた時点の値（ここより前には戻れない）
        private int _orderMaxSerial; // やり直せる上限
        private readonly HashSet<string> _orderSel = new HashSet<string>();
        private string _orderAnchor; // Shift クリックの起点
        private string _orderSearch = "";
        private Vector2 _orderScroll;
        private bool _orderDragging;
        private int _orderDropIndex = -1;
        private const float OrderRowHeight = 18f;

        // 比較用（移動したシェイプ）のキャッシュ
        private int _diffSerial = -1;
        private Mesh _diffFor;
        private HashSet<string> _moved = new HashSet<string>();
        private Dictionary<string, int> _oldIndex = new Dictionary<string, int>();
        private Dictionary<string, int> _newIndex = new Dictionary<string, int>();

        private static GUIStyle _orderRowStyle, _orderSepStyle, _orderTagStyle, _paneTitleStyle;

        private static GUIStyle OrderRowStyle => _orderRowStyle ?? (_orderRowStyle = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Clip,
            padding = new RectOffset(4, 4, 0, 0),
        });

        private static GUIStyle OrderSepStyle => _orderSepStyle ?? (_orderSepStyle = new GUIStyle(OrderRowStyle)
        {
            fontStyle = FontStyle.Bold,
        });

        private static GUIStyle OrderTagStyle => _orderTagStyle ?? (_orderTagStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            clipping = TextClipping.Clip,
        });

        private static GUIStyle PaneTitleStyle => _paneTitleStyle ?? (_paneTitleStyle = new GUIStyle(EditorStyles.miniBoldLabel)
        {
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(6, 4, 0, 0),
        });

        /// <summary>見出し（区切り）らしい名前か（---- VRCHAT ---- など）</summary>
        private static bool LooksLikeSeparator(string name)
        {
            var t = name.Trim();
            return t.Length >= 3 && (t.StartsWith("--") || t.StartsWith("==") || t.StartsWith("――") || t.StartsWith("＝＝") ||
                                     t.StartsWith("__") || t.StartsWith("**"));
        }

        private void EnsureOrderDraft()
        {
            var assigned = AssignedMesh;
            var current = CurrentOrderNames();
            // 別のファイルに切り替わった・中身（シェイプの集合）が変わったときは作り直す
            if (orderDraft != null && orderDraftFor == assigned && orderDraft.Count == current.Count &&
                !orderDraft.Except(current).Any())
                return;
            orderDraft = current;
            orderDraftFor = assigned;
            _orderBaseSerial = _orderMaxSerial = orderEditSerial;
            _orderSel.Clear();
            _diffSerial = -1;
        }

        /// <summary>並びを変更する前に呼ぶ（Unity の Undo に記録する）</summary>
        private void BeginOrderEdit(string label)
        {
            Undo.RecordObject(this, T("{0} (並び順: {1})", Title, label));
        }

        private void EndOrderEdit()
        {
            orderEditSerial++;
            _orderMaxSerial = orderEditSerial;
            _diffSerial = -1;
            Repaint();
        }

        /// <summary>退避したシェイプは見出しの直下にまとめる（保存時の並びと同じにする）</summary>
        private void NormalizeOrderDraft()
        {
            var recipe = Recipe;
            if (recipe == null) return;
            var backups = BackupPlan(recipe).Select(b => b.backup).ToList();
            if (backups.Count == 0) return;
            var header = ShapeBakeCore.NormalizeHeader(recipe.backupHeader);
            var kept = orderDraft.Where(backups.Contains).ToList();
            orderDraft.RemoveAll(backups.Contains);
            int h = orderDraft.IndexOf(header);
            if (h < 0) orderDraft.AddRange(kept);
            else orderDraft.InsertRange(h + 1, kept);
        }

        /// <summary>選択中のシェイプ（今の並び順のまま）を、指定の位置へまとめて移動する</summary>
        private void MoveSelectionTo(int index, string label)
        {
            var moving = orderDraft.Where(_orderSel.Contains).ToList();
            if (moving.Count == 0) return;
            var before = orderDraft.ToList();
            int above = orderDraft.Take(Mathf.Clamp(index, 0, orderDraft.Count)).Count(_orderSel.Contains);
            var next = orderDraft.Where(n => !_orderSel.Contains(n)).ToList();
            next.InsertRange(Mathf.Clamp(index - above, 0, next.Count), moving);
            if (next.SequenceEqual(before)) return;

            BeginOrderEdit(label);
            orderDraft = next;
            NormalizeOrderDraft();
            EndOrderEdit();
        }

        private void MoveSelectionBy(int delta)
        {
            var idx = orderDraft.Select((n, i) => (n, i)).Where(x => _orderSel.Contains(x.n)).Select(x => x.i).ToList();
            if (idx.Count == 0) return;
            if (delta < 0)
            {
                if (idx[0] > 0) MoveSelectionTo(idx[0] - 1, T("上へ"));
            }
            else if (idx[idx.Count - 1] < orderDraft.Count - 1)
            {
                MoveSelectionTo(idx[idx.Count - 1] + 2, T("下へ"));
            }
        }

        private void SetOrderDraft(List<string> next, string label)
        {
            if (next.SequenceEqual(orderDraft)) return;
            BeginOrderEdit(label);
            orderDraft = next;
            EndOrderEdit();
        }

        /// <summary>保存済みの並びと編集中の並びを比べ、移動したシェイプ（最長共通部分列に含まれないもの）を求める</summary>
        private void UpdateDiff(List<string> before)
        {
            if (_diffSerial == orderEditSerial && _diffFor == orderDraftFor) return;
            _diffSerial = orderEditSerial;
            _diffFor = orderDraftFor;
            _oldIndex = before.Select((n, k) => (n, k)).ToDictionary(x => x.n, x => x.k);
            _newIndex = orderDraft.Select((n, k) => (n, k)).ToDictionary(x => x.n, x => x.k);

            int n1 = before.Count, n2 = orderDraft.Count;
            var dp = new int[n1 + 1, n2 + 1];
            for (int a = n1 - 1; a >= 0; a--)
            for (int b = n2 - 1; b >= 0; b--)
                dp[a, b] = before[a] == orderDraft[b] ? dp[a + 1, b + 1] + 1 : Math.Max(dp[a + 1, b], dp[a, b + 1]);

            var common = new HashSet<string>();
            for (int a = 0, b = 0; a < n1 && b < n2;)
            {
                if (before[a] == orderDraft[b])
                {
                    common.Add(before[a]);
                    a++;
                    b++;
                }
                else if (dp[a + 1, b] >= dp[a, b + 1]) a++;
                else b++;
            }

            _moved = new HashSet<string>(orderDraft.Where(x => !common.Contains(x)));
        }

        private void DrawOrderTab()
        {
            TabIntro(T("シェイプキーの並び順を変更します。ドラッグ＆ドロップかボタンで並べ替えて、「並び順を保存」で反映します。"));
            EnsureOrderDraft();
            var recipe = Recipe;
            var created = new HashSet<string>(recipe != null ? ShapeBakeCore.OutputNamesOf(recipe.shapes, baseMesh) : new List<string>());
            var separators = new HashSet<string>(recipe != null
                ? recipe.shapes.Where(d => d.kind == ShapeKind.Separator).Select(Key)
                : Enumerable.Empty<string>());
            var plan = recipe != null ? BackupPlan(recipe) : new List<(string original, string backup)>();
            var backups = new HashSet<string>(plan.Select(b => b.backup));
            var replaced = new HashSet<string>(plan.Select(b => b.original));
            var header = recipe != null ? ShapeBakeCore.NormalizeHeader(recipe.backupHeader) : null;
            var before = CurrentOrderNames();
            bool changed = !orderDraft.SequenceEqual(before);
            UpdateDiff(before);

            EditorGUILayout.HelpBox(
                T("Avatar Descriptor の瞬き・視線はシェイプを番号で参照しているため、並び替えると参照がずれます。") +
                T("保存時に編集中のアバターは自動で付け直します。同じファイルを使う他のアバターは「アバター管理」で付け直してください。"),
                MessageType.Warning);

            // ---- ツールバー 1: 元に戻す / やり直す・比較
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(orderEditSerial <= _orderBaseSerial))
                    if (GUILayout.Button(new GUIContent(T("↶ 元に戻す"), T("直前の並べ替えを取り消します（Ctrl+Z）")),
                            EditorStyles.miniButtonLeft, GUILayout.Width(80)))
                        Undo.PerformUndo();
                using (new EditorGUI.DisabledScope(orderEditSerial >= _orderMaxSerial))
                    if (GUILayout.Button(new GUIContent(T("やり直す ↷"), T("取り消した並べ替えをやり直します（Ctrl+Y）")),
                            EditorStyles.miniButtonRight, GUILayout.Width(80)))
                        Undo.PerformRedo();

                GUILayout.FlexibleSpace();
                orderCompare = GUILayout.Toggle(orderCompare,
                    new GUIContent(T("変更前と比較"), T("保存済みの並び（左）と編集中の並び（右）を並べて表示します")),
                    EditorStyles.miniButton, GUILayout.Width(100));
                using (new EditorGUI.DisabledScope(!orderCompare))
                    orderChangedOnly = GUILayout.Toggle(orderChangedOnly && orderCompare,
                        new GUIContent(T("移動したものだけ（{0}）", _moved.Count), T("並べ替えで動かしたシェイプだけを表示します")),
                        EditorStyles.miniButton, GUILayout.Width(130));
            }

            // ---- ツールバー 2: 検索・移動ボタン
            using (new EditorGUILayout.HorizontalScope())
            {
                _orderSearch = EditorGUILayout.TextField(_orderSearch, EditorStyles.toolbarSearchField);
                GUILayout.Label(T("選択 {0} / 全 {1}", _orderSel.Count, orderDraft.Count), EditorStyles.miniLabel, GUILayout.Width(110));
            }

            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(_orderSel.Count == 0))
            {
                if (GUILayout.Button(T("先頭へ"), EditorStyles.miniButtonLeft)) MoveSelectionTo(0, T("先頭へ"));
                if (GUILayout.Button(T("上へ"), EditorStyles.miniButtonMid)) MoveSelectionBy(-1);
                if (GUILayout.Button(T("下へ"), EditorStyles.miniButtonMid)) MoveSelectionBy(1);
                if (GUILayout.Button(T("末尾へ"), EditorStyles.miniButtonRight)) MoveSelectionTo(orderDraft.Count, T("末尾へ"));
                if (GUILayout.Button(T("選択を外す"), EditorStyles.miniButton, GUILayout.Width(70))) _orderSel.Clear();
            }

            // ---- 一覧
            string search = _orderSearch?.Trim() ?? "";
            bool filtering = search.Length > 0 || (orderCompare && orderChangedOnly);
            bool Visible(string n) =>
                (search.Length == 0 || n.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) &&
                (!(orderCompare && orderChangedOnly) || _moved.Contains(n));
            var right = orderDraft.Where(Visible).ToList();
            var left = orderCompare ? before.Where(Visible).ToList() : null;
            int rowCount = Math.Max(right.Count, left?.Count ?? 0);

            float viewHeight = Mathf.Clamp(position.height - 380f, 200f, 2000f);
            if (orderCompare)
            {
                var titles = GUILayoutUtility.GetRect(0, 18, GUILayout.ExpandWidth(true));
                float half = (titles.width - 16) / 2f;
                GUI.Label(new Rect(titles.x, titles.y, half, 18), T("変更前（保存済み）"), PaneTitleStyle);
                GUI.Label(new Rect(titles.x + half + 4, titles.y, half, 18), T("変更後（編集中）"), PaneTitleStyle);
            }

            var outer = GUILayoutUtility.GetRect(0, viewHeight, GUILayout.ExpandWidth(true));
            GUI.Box(outer, GUIContent.none, EditorStyles.helpBox);
            float contentWidth = outer.width - 16;
            var content = new Rect(0, 0, contentWidth, rowCount * OrderRowHeight);
            _orderScroll = GUI.BeginScrollView(outer, _orderScroll, content);
            int first = Mathf.Max(0, (int)(_orderScroll.y / OrderRowHeight) - 1);
            int last = Mathf.Min(rowCount, first + (int)(viewHeight / OrderRowHeight) + 3);
            var e = Event.current;

            float paneWidth = orderCompare ? (contentWidth - 4) / 2f : contentWidth;
            float rightX = orderCompare ? paneWidth + 4 : 0;

            string Tag(string name) =>
                backups.Contains(name) ? T("退避")
                : separators.Contains(name) ? T("UI用")
                : name == header && !created.Contains(name) ? T("退避の見出し")
                : replaced.Contains(name) ? T("上書き")
                : created.Contains(name) ? T("作成") : "";

            void DrawRow(Rect r, string name, int index, bool isRight)
            {
                bool selected = _orderSel.Contains(name);
                bool moved = orderCompare && _moved.Contains(name);
                bool isSep = separators.Contains(name) || name == header || LooksLikeSeparator(name);
                if (e.type == EventType.Repaint)
                {
                    if (selected) EditorGUI.DrawRect(r, new Color(0.24f, 0.48f, 0.9f, 0.45f));
                    else if (moved) EditorGUI.DrawRect(r, isRight ? new Color(0.2f, 0.75f, 0.35f, 0.22f) : new Color(0.9f, 0.35f, 0.25f, 0.2f));
                    else if (isSep) EditorGUI.DrawRect(r, new Color(1f, 1f, 1f, 0.07f));
                    else if (index % 2 == 1) EditorGUI.DrawRect(r, new Color(1f, 1f, 1f, 0.025f));
                }

                GUI.Label(new Rect(r.x, r.y, 36, r.height), index.ToString(), OrderTagStyle);
                float tagWidth = orderCompare ? 58 : 96;
                GUI.Label(new Rect(r.x + 38, r.y, r.width - 40 - tagWidth, r.height), name, isSep ? OrderSepStyle : OrderRowStyle);

                string tag;
                if (moved && isRight && _oldIndex.TryGetValue(name, out var oi))
                    tag = index < oi ? $"↑{oi - index}" : $"↓{index - oi}";
                else if (moved && !isRight && _newIndex.TryGetValue(name, out var ni))
                    tag = $"→ {ni}";
                else tag = Tag(name);
                if (tag.Length > 0)
                    GUI.Label(new Rect(r.xMax - tagWidth - 2, r.y, tagWidth, r.height),
                        new GUIContent(tag, moved ? T("並べ替えで移動したシェイプです") : tag == T("退避") ? T("退避したシェイプは、退避用の見出しの下にまとめて置かれます") : ""),
                        OrderTagStyle);

                // クリックで選択（Ctrl で追加、Shift で範囲）。左右どちらをクリックしても同じシェイプが選ばれる
                if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
                {
                    var rows = isRight ? right : left;
                    if (e.shift && _orderAnchor != null && rows.Contains(_orderAnchor))
                    {
                        int a = rows.IndexOf(_orderAnchor), bIdx = rows.IndexOf(name);
                        if (!(e.control || e.command)) _orderSel.Clear();
                        for (int k = Mathf.Min(a, bIdx); k <= Mathf.Max(a, bIdx); k++) _orderSel.Add(rows[k]);
                    }
                    else if (e.control || e.command)
                    {
                        if (!_orderSel.Remove(name)) _orderSel.Add(name);
                        _orderAnchor = name;
                    }
                    else
                    {
                        if (!selected)
                        {
                            _orderSel.Clear();
                            _orderSel.Add(name);
                        }

                        _orderAnchor = name;
                    }

                    // ドラッグは右側（編集中）だけ。検索・絞り込み中は無効
                    _orderDragging = isRight && !filtering && _orderSel.Contains(name);
                    _orderDropIndex = -1;
                    e.Use();
                }
            }

            for (int k = first; k < last; k++)
            {
                float y = k * OrderRowHeight;
                if (left != null && k < left.Count)
                    DrawRow(new Rect(0, y, paneWidth, OrderRowHeight), left[k], _oldIndex.TryGetValue(left[k], out var li) ? li : k, false);
                if (k < right.Count)
                    DrawRow(new Rect(rightX, y, paneWidth, OrderRowHeight), right[k],
                        _newIndex.TryGetValue(right[k], out var ri) ? ri : k, true);
            }

            if (orderCompare && e.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(paneWidth + 1, _orderScroll.y, 2, viewHeight), new Color(0.5f, 0.5f, 0.5f, 0.5f));

            // ドラッグ＆ドロップ
            if (_orderDragging)
            {
                if (e.type == EventType.MouseDrag)
                {
                    _orderDropIndex = Mathf.Clamp(Mathf.RoundToInt(e.mousePosition.y / OrderRowHeight), 0, orderDraft.Count);
                    if (e.mousePosition.y - _orderScroll.y < OrderRowHeight) _orderScroll.y -= OrderRowHeight;
                    if (_orderScroll.y + viewHeight - e.mousePosition.y < OrderRowHeight) _orderScroll.y += OrderRowHeight;
                    e.Use();
                }
                else if (e.type == EventType.MouseUp)
                {
                    if (_orderDropIndex >= 0) MoveSelectionTo(_orderDropIndex, T("ドラッグで移動"));
                    _orderDragging = false;
                    _orderDropIndex = -1;
                    e.Use();
                }
                else if (e.type == EventType.Repaint && _orderDropIndex >= 0)
                {
                    EditorGUI.DrawRect(new Rect(rightX, _orderDropIndex * OrderRowHeight - 1, paneWidth, 2), new Color(0.3f, 0.65f, 1f));
                }
            }

            GUI.EndScrollView();
            if (filtering)
                EditorGUILayout.LabelField(T("検索・絞り込み中はドラッグできません。ボタンで移動してください。"), EditorStyles.miniLabel);
            else if (orderCompare)
                EditorGUILayout.LabelField(T("左が保存済み、右が編集中の並びです。動かしたシェイプは色付きで、右に移動量（↑↓）、左に移動先の番号を表示します。"),
                    EditorStyles.wordWrappedMiniLabel);

            // ---- 保存
            if (changed)
                EditorGUILayout.HelpBox(T("未保存の変更があります（移動したシェイプ {0} 個）。", _moved.Count), MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent(T("元の並びに戻す"), T("ツールの既定の並び（元の顔のデータの順・作成したシェイプ・退避用の見出し）に戻します")),
                        GUILayout.Height(26)))
                {
                    var newNames = recipe != null ? ShapeBakeCore.OutputNamesOf(recipe.shapes, baseMesh) : new List<string>();
                    SetOrderDraft(ShapeBakeCore.PlanOrder(baseMesh, newNames, plan, header, null), T("元の並びに戻す"));
                }

                using (new EditorGUI.DisabledScope(!changed))
                {
                    if (GUILayout.Button(T("変更を取り消す"), GUILayout.Height(26), GUILayout.Width(110)))
                        SetOrderDraft(before.ToList(), T("変更を取り消す"));

                    if (GUILayout.Button(T("並び順を保存"), GUILayout.Height(26), GUILayout.Width(110)))
                    {
                        if (EditorUtility.DisplayDialog(Title,
                                T("シェイプキーの並び順を保存します。\n\n") +
                                T("Avatar Descriptor の瞬き・視線はシェイプを番号で参照しているため、並び替えると参照がずれます。\n") +
                                T("・編集中のアバター：自動で付け直します\n") +
                                T("・同じファイルを使う他のアバター：「管理 › アバター管理」で付け直してください\n\n保存しますか？"),
                                T("保存する"), T("キャンセル")))
                        {
                            var order = orderDraft.ToList();
                            if (Commit(SavedShapes.Select(x => x.Clone()).ToList(), Suffix, order))
                            {
                                orderDraft = null;
                                _orderSel.Clear();
                            }
                        }

                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        // ------------------------------------------------------------------ avatars tab

        private void DrawAvatarsTab()
        {
            TabIntro(T("シーンのアバターに、作ったシェイプを反映したり、作る前の状態に戻したりします。"));
            if (DrawNoRecipe()) return;
            DrawDuplicatesSection();
        }

        // ------------------------------------------------------------------ settings tab

        private void DrawSettingsTab()
        {
            TabIntro(T("顔のデータの情報と、ふだんは変更しない設定です。"));
            var recipe = Recipe;

            Header(T("顔のデータ"));
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField(new GUIContent(T("シェイプを作る前"),
                        T("シェイプは毎回このデータ（FBX など）から作り直します。このデータ自体は変更しません")),
                    baseMesh, typeof(Mesh), false);
                EditorGUILayout.ObjectField(new GUIContent(T("保存したファイル"), T("作ったシェイプを追加した顔のデータです")),
                    recipe != null ? AssignedMesh : null, typeof(Mesh), false);
            }

            Header(T("アバターを更新したとき"));
            EditorGUILayout.LabelField(
                T("アバターを新しいバージョンに更新したときや、顔のシェイプキーを修正したときに押してください。") +
                T("作ったシェイプの設定はそのままで、最新の顔のデータに合わせて作り直します。"),
                EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(recipe == null))
                if (GUILayout.Button(new GUIContent(T("最新のアバターに合わせて作り直す"),
                        T("作ったシェイプは作成した時点の顔のデータを使っているため、アバターを更新しても自動では反映されません"))))
                {
                    Commit(SavedShapes.Select(s => s.Clone()).ToList(), Suffix);
                    GUIUtility.ExitGUI();
                }

            Header(T("このアバター用に作ったファイル"));
            var saved = SavedFilesForThisFace();
            if (saved.Count == 0)
            {
                EditorGUILayout.LabelField(T("まだありません。"), EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField(T("不要になったファイルは削除できます。使用中のファイルは削除できません。"),
                    EditorStyles.wordWrappedMiniLabel);
                foreach (var (mesh, r) in saved) DrawSavedFileRow(mesh, r, false);
            }

            Header(T("退避用の見出し"));
            EditorGUILayout.LabelField(
                T("既存のシェイプを上書きしたとき、退避した元のシェイプはこの見出しの下にまとめて置きます。") +
                T("既存の見出し（例: ---- VRCHAT ----）と同じ名前にすると、その下に置きます。"),
                EditorStyles.wordWrappedMiniLabel);
            if (string.IsNullOrEmpty(headerEdit))
                headerEdit = recipe != null ? recipe.backupHeader : ShapeRecipe.DefaultBackupHeader;
            using (new EditorGUI.DisabledScope(recipe == null))
            using (new EditorGUILayout.HorizontalScope())
            {
                headerEdit = EditorGUILayout.TextField(T("見出しの名前"), headerEdit);
                using (new EditorGUI.DisabledScope(recipe == null ||
                                                   ShapeBakeCore.NormalizeHeader(headerEdit) ==
                                                   ShapeBakeCore.NormalizeHeader(recipe.backupHeader)))
                    if (GUILayout.Button(T("適用"), GUILayout.Width(50)))
                    {
                        Commit(SavedShapes.Select(s => s.Clone()).ToList(), Suffix, null,
                            ShapeBakeCore.NormalizeHeader(headerEdit));
                        GUIUtility.ExitGUI();
                    }
            }

            Header(T("退避名の接尾辞"));
            EditorGUILayout.LabelField(T("既存のシェイプを上書きしたとき、元のシェイプはこの接尾辞を付けた名前で残します。"),
                EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(recipe == null))
            using (new EditorGUILayout.HorizontalScope())
            {
                suffixEdit = EditorGUILayout.TextField(T("接尾辞"), suffixEdit);
                using (new EditorGUI.DisabledScope(ShapeBakeCore.NormalizeSuffix(suffixEdit) == Suffix))
                    if (GUILayout.Button(T("適用"), GUILayout.Width(50)))
                    {
                        Commit(SavedShapes.Select(s => s.Clone()).ToList(), ShapeBakeCore.NormalizeSuffix(suffixEdit));
                        GUIUtility.ExitGUI();
                    }
            }

            if (recipe == null)
                EditorGUILayout.HelpBox(T("シェイプを作成すると変更できるようになります。"), MessageType.None);
        }

        private void DeleteShape(ShapeDefinition d)
        {
            if (!EditorUtility.DisplayDialog(Title,
                    T("「{0}」を削除しますか？\n既存を置き換えていた場合は元のシェイプに戻ります。\n", Key(d)) +
                    T("この操作は Undo で取り消せません（もう一度作成すれば元に戻せます）。"),
                    T("削除"), T("キャンセル")))
                return;

            var rest = SavedShapes.Where(s => s != d).Select(s => s.Clone()).ToList();
            if (Commit(rest, Suffix) && rest.Count == 0) RevertWithScope();
        }

        // ------------------------------------------------------------------ save / revert

        /// <summary>指定のシェイプ一覧で元メッシュから作り直して保存し、対象メッシュに適用する。保存できたら true</summary>
        private bool Commit(List<ShapeDefinition> definitions, string suffix, IList<string> order = null,
            string header = null)
        {
            try
            {
                return CommitCore(definitions, suffix, order, header);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog(Title, T("保存中にエラーが発生しました。詳細は Console を確認してください。\n\n") + e.Message, "OK");
                InvalidateRecipe();
                _dirty = true;
                return false;
            }
        }

        private bool CommitCore(List<ShapeDefinition> definitions, string suffix, IList<string> order, string header)
        {
            var r = targetRenderer;
            Preview.Restore();
            var assigned = r.sharedMesh;
            var recipe = ShapeRecipe.Find(assigned);
            if (recipe != null && recipe.baseMesh != baseMesh)
                throw new InvalidOperationException(T("対象メッシュの状態が変わっています。対象メッシュを指定し直してから、もう一度お試しください。"));

            var valid = definitions.Where(s => ShapeBakeCore.IsValid(s, baseMesh)).ToList();
            order = order ?? recipe?.order;
            header = ShapeBakeCore.NormalizeHeader(header ?? recipe?.backupHeader);
            EditorUtility.DisplayProgressBar(Title, T("シェイプを作成しています…"), 0.3f);
            ShapeBakeCore.GenerateResult res;
            try
            {
                res = ShapeBakeCore.Generate(baseMesh, valid, suffix, r.transform, order, header);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            var overrides = ShapeBakeCore.BackupWeightOverrides(assigned, res, CurrentWeight);
            var oldNames = ShapeNames(assigned);

            if (recipe != null)
            {
                // 並びが変わる前に、このファイルを使っているアバターの「設定時点」を記録しておく（未記録のものだけ）
                var users = RenderersUsing(assigned);
                foreach (var u in users)
                {
                    var uid = EyelidOwnerId(u);
                    if (uid != null && recipe.AvatarVersion(uid) == null) recipe.SetAvatarVersion(uid, recipe.orderVersion);
                }

                // このファイルを使っている全レンダラー（反映済みの他のアバターを含む）の値を名前で引き継ぐ
                OverwriteFile(assigned, recipe, res, baseMesh, definitions, suffix, header, res.warnings);
                bool shifted = recipe.RecordOrder(oldNames, ShapeNames(assigned));

                // 瞬き・視線の付け直しは、編集中のアバターだけ自動で行う
                RemapEyelids(r, oldNames, res.warnings);
                var id = EyelidOwnerId(r);
                if (id != null) recipe.SetAvatarVersion(id, recipe.orderVersion);

                int others = users.Count(u => u != r && JudgeEyelids(recipe, u, out _) == EyelidState.Shifted);
                if (shifted && others > 0)
                    res.warnings.Add(T("シェイプの並びが変わったため、同じファイルを使う他のアバター {0} 体の瞬き・視線の設定がずれています。", others) +
                                     T("「管理 › アバター管理」で付け直してください。"));
                EditorUtility.SetDirty(recipe);
                AssetDatabase.SaveAssets();
            }
            else
            {
                if (!AssetDatabase.IsValidFolder(DefaultFolder))
                    AssetDatabase.CreateFolder("Assets", Path.GetFileName(DefaultFolder));
                var path = EditorUtility.SaveFilePanelInProject(T("メッシュを保存"), baseMesh.name + "_BSE", "asset",
                    T("シェイプを追加したメッシュの保存先"), DefaultFolder);
                if (string.IsNullOrEmpty(path))
                {
                    Object.DestroyImmediate(res.mesh);
                    _dirty = true;
                    return false;
                }

                res.mesh.name = Path.GetFileNameWithoutExtension(path);
                AssetDatabase.CreateAsset(res.mesh, path);
                recipe = CreateInstance<ShapeRecipe>();
                recipe.name = "Recipe";
                WriteRecipe(recipe, baseMesh, definitions, suffix, res.order, header);
                recipe.RecordOrder(ShapeNames(res.mesh), ShapeNames(res.mesh));
                AssetDatabase.AddObjectToAsset(recipe, res.mesh);

                Undo.RecordObject(r, Title);
                ShapeBakeCore.SwapMeshKeepingWeights(r, res.mesh, overrides);
                PrefabUtility.RecordPrefabInstancePropertyModifications(r);
                RemapEyelids(r, oldNames, res.warnings);
                var id = EyelidOwnerId(r);
                if (id != null) recipe.SetAvatarVersion(id, recipe.orderVersion);
                EditorUtility.SetDirty(recipe);
                AssetDatabase.SaveAssets();
            }

            foreach (var w in res.warnings) Debug.LogWarning($"[{Title}] {w}");
            Debug.Log(T("[{0}] 保存しました: {1}", Title, AssetDatabase.GetAssetPath(r.sharedMesh)), r.sharedMesh);
            InvalidateRecipe();
            InvalidateDuplicates();
            _synced = false;
            suffixEdit = suffix;
            _dirty = true;
            return true;
        }

        /// <summary>
        /// 退避名の付け替え。今回なくなる退避先の値は元の名前へ戻し、新しく退避する元シェイプの値は退避先へ移す
        /// （見た目を変えないため）。
        /// </summary>
        private static Dictionary<string, float> RemapBackupWeights(Dictionary<string, float> weights,
            List<(string original, string backup)> oldBackups, List<(string original, string backup)> newBackups)
        {
            foreach (var (original, backup) in oldBackups)
                if (weights.TryGetValue(backup, out var bw) && !newBackups.Any(b => b.backup == backup))
                {
                    weights[original] = bw;
                    weights.Remove(backup);
                }

            foreach (var (original, backup) in newBackups)
                if (!weights.ContainsKey(backup))
                {
                    weights[backup] = weights.TryGetValue(original, out var ow) ? ow : 0f;
                    weights[original] = 0f;
                }

            return weights;
        }

        /// <summary>開いているシーンで指定のメッシュを使っている SkinnedMeshRenderer（プレビュー用の非表示オブジェクトは除く）</summary>
        private static List<SkinnedMeshRenderer> RenderersUsing(Mesh mesh) =>
            Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>()
                .Where(x => x.sharedMesh == mesh && IsUserRenderer(x)).ToList();

        /// <summary>
        /// 保存ファイルを作り直した内容で上書きし、そのファイルを使っているレンダラーの値・瞬き設定を名前で引き継ぐ。
        /// </summary>
        private static void OverwriteFile(Mesh file, ShapeRecipe recipe, ShapeBakeCore.GenerateResult res, Mesh face,
            List<ShapeDefinition> definitions, string suffix, string header, List<string> warnings)
        {
            var users = RenderersUsing(file);
            var oldBackups = recipe.baseMesh != null ? ShapeBakeCore.PlanBackups(recipe) : new List<(string, string)>();
            var userWeights = users.ToDictionary(u => u, u => RemapBackupWeights(WeightsByName(u), oldBackups, res.backups));

            res.mesh.name = file.name;
            EditorUtility.CopySerialized(res.mesh, file);
            Object.DestroyImmediate(res.mesh);
            WriteRecipe(recipe, face, definitions, suffix, res.order, header);
            EditorUtility.SetDirty(file);

            foreach (var u in users)
            {
                Undo.RecordObject(u, Title);
                var weights = userWeights[u];
                u.sharedMesh = null;
                u.sharedMesh = file;
                for (int i = 0; i < file.blendShapeCount; i++)
                    u.SetBlendShapeWeight(i, weights.TryGetValue(file.GetBlendShapeName(i), out var w) ? w : 0f);
                PrefabUtility.RecordPrefabInstancePropertyModifications(u);
            }

            warnings.AddRange(res.warnings);
        }

        /// <summary>Avatar Descriptor の瞬き・視線がこのレンダラーのシェイプを使っていれば、その Descriptor の ID</summary>
        private static string EyelidOwnerId(SkinnedMeshRenderer r)
        {
#if BSE_VRCSDK
            if (r == null) return null;
            var d = r.GetComponentInParent<VRCAvatarDescriptor>(true);
            if (d == null || d.customEyeLookSettings.eyelidType != VRCAvatarDescriptor.EyelidType.Blendshapes ||
                d.customEyeLookSettings.eyelidsSkinnedMesh != r)
                return null;
            return GlobalObjectId.GetGlobalObjectIdSlow(d).ToString();
#else
            return null;
#endif
        }

        private static string[] ShapeNames(Mesh m) =>
            m == null ? Array.Empty<string>() : Enumerable.Range(0, m.blendShapeCount).Select(m.GetBlendShapeName).ToArray();

        /// <summary>
        /// Avatar Descriptor の瞬き・視線（シェイプを番号で参照している）を名前で付け直す。
        /// シェイプの並びが変わっても、同じ名前のシェイプを指し続けるようにする。
        /// </summary>
        private static void RemapEyelids(SkinnedMeshRenderer r, string[] oldNames, List<string> warnings)
        {
#if BSE_VRCSDK
            var d = r.GetComponentInParent<VRCAvatarDescriptor>(true);
            if (d == null) return;
            var eye = d.customEyeLookSettings;
            if (eye.eyelidsSkinnedMesh != r || eye.eyelidsBlendshapes == null) return;

            var mesh = r.sharedMesh;
            var arr = (int[])eye.eyelidsBlendshapes.Clone();
            bool changed = false;
            for (int i = 0; i < arr.Length; i++)
            {
                int idx = arr[i];
                if (idx < 0 || idx >= oldNames.Length) continue;
                int ni = mesh != null ? mesh.GetBlendShapeIndex(oldNames[idx]) : -1;
                if (ni == idx) continue;
                arr[i] = ni;
                changed = true;
                if (ni < 0)
                    warnings.Add(T("Avatar Descriptor の瞬き・視線で使っていた「{0}」が無くなったため、割り当てを解除しました。", oldNames[idx]));
            }

            if (!changed) return;
            Undo.RecordObject(d, Title);
            eye.eyelidsBlendshapes = arr;
            d.customEyeLookSettings = eye;
            PrefabUtility.RecordPrefabInstancePropertyModifications(d);
#endif
        }

        private static void WriteRecipe(ShapeRecipe recipe, Mesh face, List<ShapeDefinition> definitions, string suffix,
            List<string> order, string header)
        {
            recipe.baseMesh = face;
            recipe.backupSuffix = ShapeBakeCore.NormalizeSuffix(suffix);
            recipe.order = order.ToList();
            recipe.backupHeader = ShapeBakeCore.NormalizeHeader(header);
            recipe.shapes = definitions.Where(s => !string.IsNullOrWhiteSpace(s.outputName)).Select(s => s.Clone()).ToList();
            EditorUtility.SetDirty(recipe);
        }

        /// <summary>
        /// 反映前の状態に戻す（記録が無ければ作る前の顔）。同じファイルを他のアバターも使っていれば、戻す範囲を選んでもらう。
        /// </summary>
        private void RevertWithScope()
        {
            Preview.Restore();
            var file = targetRenderer.sharedMesh;
            var users = ShapeRecipe.Find(file) != null ? RenderersUsing(file) : new List<SkinnedMeshRenderer>();
            users.Remove(targetRenderer);
            users.Insert(0, targetRenderer);

            if (users.Count <= 1)
            {
                RevertRenderers(users);
                return;
            }

            int choice = EditorUtility.DisplayDialogComplex(Title,
                T("反映前の状態に戻します（反映前の記録が無いアバターは、シェイプを作る前の顔に戻ります）。\n\n") +
                T("このファイル（{0}）は、このアバターを含めて {1} 体のアバターが使っています。\n", file.name, users.Count) +
                T("どの範囲を戻しますか？"),
                T("このアバターだけ戻す"), T("キャンセル"), T("全アバター（{0} 体）を戻す", users.Count));
            if (choice == 1) return;
            RevertRenderers(choice == 2 ? users : users.Take(1).ToList());
        }

        /// <summary>
        /// 指定のアバターを、シェイプを作る前の顔のデータに戻す。
        /// 退避していたシェイプの値は元の名前へ戻し、Avatar Descriptor の瞬き・視線も名前で付け直す。
        /// </summary>
        private void RevertRenderers(List<SkinnedMeshRenderer> renderers)
        {
            var warnings = new List<string>();
            Undo.SetCurrentGroupName(Title + T(" (元に戻す)"));
            int group = Undo.GetCurrentGroup();
            int count = 0;

            foreach (var r in renderers)
            {
                if (r == null) continue;
                var current = r.sharedMesh;
                var recipe = ShapeRecipe.Find(current);

                // 反映前の記録があれば、記録どおりの状態（使っていたファイル・値・瞬き/視線）に戻す
                var point = recipe?.LatestRestorePoint(RendererId(r));
                if (point != null)
                {
                    Undo.RecordObject(recipe, Title + T(" (反映前に戻す)"));
                    if (RestoreFromPoint(r, point, recipe, warnings))
                    {
                        EditorUtility.SetDirty(recipe);
                        count++;
                        continue;
                    }

                    point.restored = true; // ファイルが無くなっている → 作る前の顔に戻す
                }

                var face = recipe != null && recipe.baseMesh != null ? recipe.baseMesh : (r == targetRenderer ? baseMesh : null);
                if (face == null || face == current) continue;

                var oldNames = ShapeNames(current);
                var overrides = new Dictionary<string, float>();
                if (recipe != null && recipe.baseMesh != null)
                {
                    var weights = WeightsByName(r);
                    foreach (var (original, backup) in ShapeBakeCore.PlanBackups(recipe))
                        if (weights.TryGetValue(backup, out var w))
                            overrides[original] = w;
                }

                var id = EyelidOwnerId(r);
                if (recipe != null && id != null)
                {
                    recipe.RemoveAvatar(id);
                    EditorUtility.SetDirty(recipe);
                }

                Undo.RecordObject(r, Title + T(" (元に戻す)"));
                ShapeBakeCore.SwapMeshKeepingWeights(r, face, overrides);
                PrefabUtility.RecordPrefabInstancePropertyModifications(r);
                RemapEyelids(r, oldNames, warnings);
                count++;
            }

            AssetDatabase.SaveAssets();
            Undo.CollapseUndoOperations(group);
            foreach (var w in warnings) Debug.LogWarning($"[{Title}] {w}");
            if (count > 0) Debug.Log(T("[{0}] {1} 体のアバターを反映前に戻しました。", Title, count));
            InvalidateRecipe();
            InvalidateDuplicates();
            _synced = false;
            _dirty = true;
        }

        private static Dictionary<string, float> WeightsByName(SkinnedMeshRenderer r)
        {
            var dict = new Dictionary<string, float>();
            var m = r.sharedMesh;
            if (m != null)
                for (int i = 0; i < m.blendShapeCount; i++)
                    dict[m.GetBlendShapeName(i)] = r.GetBlendShapeWeight(i);
            return dict;
        }

        /// <summary>作成済みシェイプを作成タブに読み込む。±100 を超える値があれば限界突破を自動でオンにする</summary>
        private void LoadDraft(ShapeDefinition d)
        {
            draft = d.Clone();
            if (draft.sources.Any(x => Mathf.Abs(x.value) > 100f))
            {
                overLimit = true;
                overLimitMax = Mathf.Clamp(Mathf.Max(overLimitMax,
                    Mathf.CeilToInt(draft.sources.Max(x => Mathf.Abs(x.value)))), 100, 1000);
            }
        }

        /// <summary>今の値を打ち消す値（−現在値）。限界突破オフなら ±100 に収める</summary>
        private float CancelValue(string name)
        {
            float limit = overLimit ? overLimitMax : 100f;
            return Mathf.Clamp(-CurrentWeight(name), -limit, limit);
        }

        /// <summary>編集対象に選べるシェイプ（元メッシュのシェイプ＋このツールで新しく作ったシェイプ）</summary>
        private List<(string name, string label)> EditableShapes()
        {
            var created = new HashSet<string>(SavedShapes.Select(Key));
            var items = new List<(string, string)>();
            for (int i = 0; i < baseMesh.blendShapeCount; i++)
            {
                var n = baseMesh.GetBlendShapeName(i);
                items.Add((n, created.Contains(n) ? n + T("    (作成済み)") : n));
            }

            foreach (var n in created.Where(n => baseMesh.GetBlendShapeIndex(n) < 0))
                items.Add((n, n + T("    (作成済み)")));
            return items;
        }

        /// <summary>
        /// 既存シェイプを編集対象にする。作成済みのシェイプならその設定を読み込み、
        /// それ以外は同じ名前で出力し、元にするシェイプにそのシェイプを 100 で設定する。
        /// </summary>
        private void SelectExisting(string name)
        {
            var saved = SavedShapes.FirstOrDefault(d => Key(d) == name);
            if (saved != null)
            {
                LoadDraft(saved);
                return;
            }

            draft = new ShapeDefinition
            {
                outputName = name,
                sources = new List<SourceShape> { new SourceShape { shapeName = name, value = 100f } },
            };
        }

        private void ShapePicker(string current, string empty, Action<string> onSelect, GUIStyle style,
            params GUILayoutOption[] options)
        {
            var label = new GUIContent(string.IsNullOrEmpty(current) ? empty : current);
            var rect = GUILayoutUtility.GetRect(label, style, options);
            if (!GUI.Button(rect, label, style)) return;

            var items = new List<(string name, string label)>();
            for (int i = 0; i < baseMesh.blendShapeCount; i++)
            {
                var n = baseMesh.GetBlendShapeName(i);
                float w = CurrentWeight(n);
                bool nz = !Mathf.Approximately(w, 0f);
                if (nonZeroOnly && !nz) continue;
                items.Add((n, nz ? $"{n}    ({w:0.#})" : n));
            }

            new ShapeNameDropdown(items, onSelect,
                nonZeroOnly ? T("{0} 上で値が入っているシェイプはありません", targetRenderer.name) : T("シェイプキーがありません")).Show(rect);
        }

        // ------------------------------------------------------------------ preview state

        /// <summary>
        /// プレビュー用に差し替えたメッシュの管理。割り当て中のメッシュを複製し、作成中のシェイプを末尾に 1 つ追加して表示する。
        /// シーン保存・再生・アセンブリリロード・アップロード前に必ず元に戻す。
        /// </summary>
        [InitializeOnLoad]
        internal static class Preview
        {
            private const string PreviewShapeName = "__BlendshapeEditor_Preview__";

            public static BlendshapeEditorWindow Owner;

            private static SkinnedMeshRenderer _renderer;
            private static Mesh _original;
            private static Mesh _preview;

            // Undo で過去のプレビュー用メッシュが戻ってきたときに元へ戻すための記録
            private static SkinnedMeshRenderer _lastRenderer;
            private static Mesh _lastOriginal;

            private static bool IsPreviewMesh(Mesh m) =>
                m != null && (m.hideFlags & HideFlags.DontSave) != 0 && m.name.EndsWith(" (Preview)");
            private static readonly Dictionary<int, float> Muted = new Dictionary<int, float>(); // index → 元の値

            public static bool IsShowing => _preview != null && _renderer != null && _renderer.sharedMesh == _preview;

            static Preview()
            {
                EditorSceneManager.sceneSaving += (_, __) => Restore();
                EditorSceneManager.sceneSaved += _ => Owner?.MarkDirty();
                PrefabStage.prefabSaving += _ => Restore();
                PrefabStage.prefabSaved += _ => Owner?.MarkDirty();
                AssemblyReloadEvents.beforeAssemblyReload += Restore;
                Undo.undoRedoPerformed += () =>
                {
                    Restore();
                    RepairAfterUndo();
                    if (Owner == null) return;
                    Owner.InvalidateRecipe();
                    Owner.MarkDirty();
                    Owner.Repaint();
                };
                EditorApplication.quitting += Restore;
                SceneView.duringSceneGui += DrawSceneStatus;
                EditorApplication.playModeStateChanged += s =>
                {
                    if (s == PlayModeStateChange.ExitingEditMode) Restore();
                    if (s == PlayModeStateChange.EnteredEditMode) Owner?.MarkDirty();
                };
            }

            public static Mesh OriginalOf(SkinnedMeshRenderer r) => r != null && r == _renderer ? _original : null;

            /// <summary>プレビュー中だけ 0 にしているシェイプなら、本来の値を返す</summary>
            public static float? MutedWeight(SkinnedMeshRenderer r, string name)
            {
                if (r == null || r != _renderer || _original == null) return null;
                int i = _original.GetBlendShapeIndex(name);
                return i >= 0 && Muted.TryGetValue(i, out var w) ? w : (float?)null;
            }

            /// <summary>作成中のシェイプ（新シェイプ 100 のときの変位）を末尾に追加したメッシュで表示する</summary>
            public static void Show(SkinnedMeshRenderer r, Vector3[] dv, Vector3[] dn, Vector3[] dt, float weight,
                IEnumerable<string> muteNames)
            {
                if (_renderer != r || _original == null ||
                    (r.sharedMesh != _preview && r.sharedMesh != _original))
                {
                    Restore();
                    _renderer = r;
                    _original = r.sharedMesh;
                    _lastRenderer = r;
                    _lastOriginal = _original;
                }

                // 今のウェイト（プレビュー中は元メッシュと同じインデックス）を控える
                int count = _original.blendShapeCount;
                var weights = new float[count];
                for (int i = 0; i < count; i++) weights[i] = r.GetBlendShapeWeight(i);

                // 複製はネイティブ側の一括コピーなので、シェイプを 1 つずつ積み直すより大幅に軽い
                var mesh = Object.Instantiate(_original);
                mesh.name = _original.name + " (Preview)";
                mesh.hideFlags = HideFlags.DontSave;
                mesh.AddBlendShapeFrame(PreviewShapeName, 100f, dv, dn, dt);

                r.sharedMesh = mesh;
                for (int i = 0; i < count; i++) r.SetBlendShapeWeight(i, weights[i]);
                if (_preview != null) Object.DestroyImmediate(_preview);
                _preview = mesh;

                // 同じ名前の作成済みシェイプは二重にかからないよう 0 にする（外れたものは元に戻す）
                var mute = new HashSet<int>(muteNames.Select(n => _original.GetBlendShapeIndex(n)).Where(i => i >= 0));
                foreach (var i in Muted.Keys.Where(i => !mute.Contains(i)).ToList())
                {
                    r.SetBlendShapeWeight(i, Muted[i]);
                    Muted.Remove(i);
                }

                foreach (var i in mute)
                {
                    if (!Muted.ContainsKey(i)) Muted[i] = r.GetBlendShapeWeight(i);
                    r.SetBlendShapeWeight(i, 0f);
                }

                SetWeight(weight);
            }

            /// <summary>
            /// シーンビューに状態を表示する。プレビュー中はオレンジの帯と枠、そうでなければ灰色の帯。
            /// ウィンドウで対象メッシュを選んでいるときだけ表示する。
            /// </summary>
            private static void DrawSceneStatus(SceneView view)
            {
                if (Owner == null || Owner.targetRenderer == null || Event.current.type != EventType.Repaint) return;

                var state = Owner.CurrentPreviewState();
                bool showing = state == PreviewState.Showing;
                string suffix = $"　／　{Owner.targetRenderer.name}";
                string text = Owner.StatusText(state) + suffix;

                Handles.BeginGUI();
                var size = StatusStyle.CalcSize(new GUIContent(text)) + new Vector2(4f, 0f);
                var area = view.camera.pixelRect;
                float ppp = EditorGUIUtility.pixelsPerPoint;
                float w = area.width / ppp, h = area.height / ppp;

                if (showing)
                {
                    const float t = 4f;
                    EditorGUI.DrawRect(new Rect(0, 0, w, t), PreviewColor);
                    EditorGUI.DrawRect(new Rect(0, h - t, w, t), PreviewColor);
                    EditorGUI.DrawRect(new Rect(0, 0, t, h), PreviewColor);
                    EditorGUI.DrawRect(new Rect(w - t, 0, t, h), PreviewColor);
                }

                var rect = new Rect((w - size.x) / 2f, 10f, size.x, 26f);
                Owner.DrawStatusBar(rect, state, suffix);
                Handles.EndGUI();
            }

            public static void SetWeight(float weight)
            {
                if (!IsShowing) return;
                _renderer.SetBlendShapeWeight(_preview.blendShapeCount - 1, weight);
                SceneView.RepaintAll();
            }

            /// <summary>Undo で破棄済みのプレビュー用メッシュが割り当てられた場合、元のメッシュに戻す</summary>
            private static void RepairAfterUndo()
            {
                if (_lastRenderer == null || _lastOriginal == null) return;
                // 破棄済みのメッシュ（Unity 上は null 扱いだが参照は残っている）か、プレビュー用メッシュのときだけ直す
                var cur = _lastRenderer.sharedMesh;
                bool destroyed = !ReferenceEquals(cur, null) && cur == null;
                if (!destroyed && !IsPreviewMesh(cur)) return;
                _lastRenderer.sharedMesh = _lastOriginal;
                SceneView.RepaintAll();
            }

            public static void Restore()
            {
                // 破棄済み（Undo で戻ってきた古いプレビュー用メッシュ）も含めて元に戻す
                if (_renderer != null && _original != null && _preview != null &&
                    (_renderer.sharedMesh == null || IsPreviewMesh(_renderer.sharedMesh)) &&
                    _renderer.sharedMesh != _preview)
                    _renderer.sharedMesh = _original;

                if (_renderer != null && _original != null && _renderer.sharedMesh == _preview && _preview != null)
                {
                    int count = _original.blendShapeCount;
                    var weights = new float[count];
                    for (int i = 0; i < count; i++) weights[i] = _renderer.GetBlendShapeWeight(i);
                    foreach (var kv in Muted)
                        if (kv.Key < count)
                            weights[kv.Key] = kv.Value;
                    _renderer.sharedMesh = _original;
                    for (int i = 0; i < count; i++) _renderer.SetBlendShapeWeight(i, weights[i]);
                }

                if (_preview != null) Object.DestroyImmediate(_preview);
                Muted.Clear();
                _renderer = null;
                _original = null;
                _preview = null;
                SceneView.RepaintAll();
            }
        }
    }

#if BSE_VRCSDK
    /// <summary>VRChat のアップロード開始時（アバター複製前）にプレビューを解除する</summary>
    internal class RestorePreviewOnBuild : VRC.SDKBase.Editor.BuildPipeline.IVRCSDKBuildRequestedCallback
    {
        public int callbackOrder => -10000;

        public bool OnBuildRequested(VRC.SDKBase.Editor.BuildPipeline.VRCSDKRequestedBuildType requestedBuildType)
        {
            BlendshapeEditorWindow.Preview.Restore();
            return true;
        }
    }
#endif

    internal class ShapeNameDropdown : AdvancedDropdown
    {
        private readonly List<(string name, string label)> _items;
        private readonly Action<string> _onSelect;
        private readonly string _emptyMessage;

        public ShapeNameDropdown(List<(string name, string label)> items, Action<string> onSelect, string emptyMessage)
            : base(new AdvancedDropdownState())
        {
            _items = items;
            _onSelect = onSelect;
            _emptyMessage = emptyMessage;
            minimumSize = new Vector2(280, 420);
        }

        protected override AdvancedDropdownItem BuildRoot()
        {
            var root = new AdvancedDropdownItem(T("シェイプキー"));
            if (_items.Count == 0)
                root.AddChild(new AdvancedDropdownItem(_emptyMessage) { id = -1, enabled = false });
            for (int i = 0; i < _items.Count; i++) root.AddChild(new AdvancedDropdownItem(_items[i].label) { id = i });
            return root;
        }

        protected override void ItemSelected(AdvancedDropdownItem item)
        {
            if (item.id >= 0 && item.id < _items.Count) _onSelect(_items[item.id].name);
        }
    }
}
