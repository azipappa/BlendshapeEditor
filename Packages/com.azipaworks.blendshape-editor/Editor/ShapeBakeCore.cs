using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;
using static AzipaWorks.BlendshapeEditor.BseLocalization;

namespace AzipaWorks.BlendshapeEditor
{
    internal class BakedShape
    {
        public string name;
        public List<(float weight, Vector3[] v, Vector3[] n, Vector3[] t)> frames =
            new List<(float, Vector3[], Vector3[], Vector3[])>();
    }

    internal static class ShapeBakeCore
    {
        internal struct Frame
        {
            public float weight;
            public Vector3[] v, n, t;
        }

        internal static Frame[] ReadFrames(Mesh mesh, int index)
        {
            int vc = mesh.vertexCount;
            var frames = new Frame[mesh.GetBlendShapeFrameCount(index)];
            for (int i = 0; i < frames.Length; i++)
            {
                frames[i].weight = mesh.GetBlendShapeFrameWeight(index, i);
                frames[i].v = new Vector3[vc];
                frames[i].n = new Vector3[vc];
                frames[i].t = new Vector3[vc];
                mesh.GetBlendShapeFrameVertices(index, i, frames[i].v, frames[i].n, frames[i].t);
            }

            return frames;
        }

        /// <summary>
        /// シェイプをウェイト w で評価した変位を acc に加算する。
        /// 中間フレームは線形補間、範囲外（負の値や 100 超）は端の区間で線形外挿する。
        /// </summary>
        internal static void Accumulate(Frame[] f, float w, Vector3[] accV, Vector3[] accN, Vector3[] accT)
        {
            if (f.Length == 0 || w == 0f) return;
            int a = -1, b;
            float ca = 0f, cb;
            if (f.Length == 1 || w <= f[0].weight)
            {
                b = 0;
                cb = f[0].weight > 0f ? w / f[0].weight : w / 100f;
            }
            else
            {
                int k = 1;
                while (k < f.Length - 1 && f[k].weight < w) k++;
                float span = f[k].weight - f[k - 1].weight;
                float t = span > 0f ? (w - f[k - 1].weight) / span : 1f;
                a = k - 1;
                ca = 1f - t;
                b = k;
                cb = t;
            }

            for (int i = 0; i < accV.Length; i++)
            {
                accV[i] += f[b].v[i] * cb;
                accN[i] += f[b].n[i] * cb;
                accT[i] += f[b].t[i] * cb;
            }

            if (a < 0 || ca == 0f) return;
            for (int i = 0; i < accV.Length; i++)
            {
                accV[i] += f[a].v[i] * ca;
                accN[i] += f[a].n[i] * ca;
                accT[i] += f[a].t[i] * ca;
            }
        }

        /// <summary>
        /// 新しいシェイプを計算する。新シェイプの値 x のとき、各元シェイプが value × x/100 だけ動いたのと同じ形になる。
        /// 元シェイプに中間フレームがある場合は 10 フレームに分けて焼き込み、途中の形も再現する。
        /// </summary>
        /// <summary>見出し用の空のシェイプ（変形しない 1 フレーム）</summary>
        public static BakedShape EmptyShape(string name, int vertexCount)
        {
            var shape = new BakedShape { name = name };
            shape.frames.Add((100f, new Vector3[vertexCount], new Vector3[vertexCount], new Vector3[vertexCount]));
            return shape;
        }

        public static List<BakedShape> Compute(Mesh mesh, string outputName, IEnumerable<SourceShape> sources,
            SideSplit split, float[] leftMask)
        {
            var resolved = new List<(Frame[] frames, float value)>();
            foreach (var s in sources)
            {
                int idx = string.IsNullOrEmpty(s.shapeName) ? -1 : mesh.GetBlendShapeIndex(s.shapeName);
                if (idx >= 0) resolved.Add((ReadFrames(mesh, idx), s.value));
            }

            int vc = mesh.vertexCount;
            int frameCount = resolved.Any(r => r.frames.Length > 1) ? 10 : 1;
            var full = new BakedShape { name = outputName };
            for (int k = 1; k <= frameCount; k++)
            {
                float p = (float)k / frameCount;
                var v = new Vector3[vc];
                var n = new Vector3[vc];
                var t = new Vector3[vc];
                foreach (var (frames, value) in resolved) Accumulate(frames, value * p, v, n, t);
                full.frames.Add((100f * p, v, n, t));
            }

            switch (split)
            {
                case SideSplit.LeftOnly: return new List<BakedShape> { Masked(full, outputName, leftMask, false) };
                case SideSplit.RightOnly: return new List<BakedShape> { Masked(full, outputName, leftMask, true) };
                default: return new List<BakedShape> { full };
            }
        }

        public static IEnumerable<string> OutputNames(string outputName, SideSplit split)
        {
            yield return outputName;
        }

        private static BakedShape Masked(BakedShape src, string name, float[] leftMask, bool right)
        {
            var dst = new BakedShape { name = name };
            foreach (var (w, v, n, t) in src.frames)
            {
                var mv = new Vector3[v.Length];
                var mn = new Vector3[v.Length];
                var mt = new Vector3[v.Length];
                for (int i = 0; i < v.Length; i++)
                {
                    float m = right ? 1f - leftMask[i] : leftMask[i];
                    mv[i] = v[i] * m;
                    mn[i] = n[i] * m;
                    mt[i] = t[i] * m;
                }

                dst.frames.Add((w, mv, mn, mt));
            }

            return dst;
        }

        /// <summary>
        /// キャラの左側 = 1 のマスク。アバタールート空間の X=0 を境界とし、blendWidth の幅でなめらかに切り替える
        /// （アバターは +Z を向いている前提なので、キャラの左は −X）。
        /// </summary>
        public static float[] LeftMask(Mesh mesh, Transform rendererTransform, Transform avatarRoot, float blendWidth)
        {
            var verts = mesh.vertices;
            var mask = new float[verts.Length];
            var m = avatarRoot.worldToLocalMatrix * rendererTransform.localToWorldMatrix;
            for (int i = 0; i < verts.Length; i++)
            {
                float x = m.MultiplyPoint3x4(verts[i]).x;
                mask[i] = blendWidth > 1e-6f ? Mathf.Clamp01(0.5f - x / blendWidth) : (x < 0f ? 1f : x > 0f ? 0f : 0.5f);
            }

            return mask;
        }

        internal class GenerateResult
        {
            public Mesh mesh;

            /// <summary>上書きした元シェイプ名 → 退避先の名前</summary>
            public readonly List<(string original, string backup)> backups = new List<(string, string)>();

            public readonly List<string> warnings = new List<string>();

            /// <summary>完成したシェイプキーの並び</summary>
            public List<string> order = new List<string>();
        }

        public static bool IsValid(ShapeDefinition d, Mesh baseMesh)
        {
            if (d == null || string.IsNullOrWhiteSpace(d.outputName) || baseMesh == null) return false;
            if (d.kind == ShapeKind.Separator) return true;
            return d.sources.Any(s => !string.IsNullOrEmpty(s.shapeName) && baseMesh.GetBlendShapeIndex(s.shapeName) >= 0);
        }

        /// <summary>定義が作るシェイプ名</summary>
        public static IEnumerable<string> OutputNames(ShapeDefinition d) =>
            d.kind == ShapeKind.Separator
                ? new[] { d.outputName.Trim() }
                : OutputNames(d.outputName.Trim(), d.split);

        /// <summary>有効な定義が作るシェイプ名（定義の順・重複なし）</summary>
        public static List<string> OutputNamesOf(IEnumerable<ShapeDefinition> definitions, Mesh baseMesh) =>
            definitions.Where(d => IsValid(d, baseMesh)).SelectMany(OutputNames).Where(n => n.Length > 0).Distinct().ToList();

        public static string NormalizeHeader(string header) =>
            string.IsNullOrWhiteSpace(header) ? ShapeRecipe.DefaultBackupHeader : header.Trim();

        /// <summary>
        /// 完成後のシェイプキーの並びを決める。
        /// ・保存済みの並び（order）があればそれに従い、無い名前は既定の並びでの前後関係から近い位置に差し込む
        /// ・新しく作ったシェイプで位置が決まっていないものは、退避用の見出しの上（無ければ末尾）
        /// ・退避したシェイプは必ず退避用の見出しの直下にまとめる（見出しが無ければ自動で作る）
        /// </summary>
        public static List<string> PlanOrder(Mesh baseMesh, IList<string> newNames,
            List<(string original, string backup)> backups, string backupHeader, IList<string> order)
        {
            var baseNames = Enumerable.Range(0, baseMesh.blendShapeCount).Select(baseMesh.GetBlendShapeName).ToList();
            var backupNames = backups.Select(b => b.backup).ToList();
            string header = backups.Count > 0 ? NormalizeHeader(backupHeader) : null;
            bool createHeader = header != null && !baseNames.Contains(header) && !newNames.Contains(header);

            var defaults = new List<string>(baseNames);
            defaults.AddRange(newNames.Where(n => !baseNames.Contains(n)));
            if (createHeader) defaults.Add(header);
            defaults.AddRange(backupNames);
            var items = new HashSet<string>(defaults);

            List<string> result;
            if (order == null || order.Count == 0)
            {
                result = new List<string>(defaults);
            }
            else
            {
                result = order.Where(items.Contains).Distinct().ToList();
                var present = new HashSet<string>(result);
                var created = new HashSet<string>(newNames);
                for (int i = 0; i < defaults.Count; i++)
                {
                    var name = defaults[i];
                    if (present.Contains(name)) continue;
                    int at;
                    if (created.Contains(name) && !baseNames.Contains(name))
                    {
                        // 位置の決まっていない新しいシェイプ → 見出しの上、無ければ末尾
                        int h = header != null ? result.IndexOf(header) : -1;
                        at = h >= 0 ? h : result.Count;
                    }
                    else
                    {
                        // 既定の並びで直前にあるものの下に入れる
                        at = 0;
                        for (int k = i - 1; k >= 0; k--)
                        {
                            int idx = result.IndexOf(defaults[k]);
                            if (idx < 0) continue;
                            at = idx + 1;
                            break;
                        }
                    }

                    result.Insert(at, name);
                    present.Add(name);
                }
            }

            // 退避したシェイプは見出しの直下にまとめる（順番は今の並びを保つ）
            if (header != null)
            {
                var kept = result.Where(backupNames.Contains).ToList();
                result.RemoveAll(backupNames.Contains);
                int h = result.IndexOf(header);
                if (h < 0)
                {
                    result.Add(header);
                    h = result.Count - 1;
                }

                result.InsertRange(h + 1, kept);
            }

            return result;
        }

        public static string NormalizeSuffix(string suffix) => string.IsNullOrEmpty(suffix) ? "_orig" : suffix;

        /// <summary>
        /// 元メッシュから全シェイプを作り直したメッシュを返す。元シェイプは常に元メッシュのデータを使う。
        /// ・元メッシュに無い名前 → 末尾に追加
        /// ・元メッシュにある名前 → その位置に新シェイプを入れ、元のシェイプは「名前+接尾辞」で末尾に退避
        /// </summary>
        public static GenerateResult Generate(Mesh baseMesh, IEnumerable<ShapeDefinition> definitions,
            string backupSuffix, Transform rendererTransform, IList<string> order = null, string backupHeader = null)
        {
            var res = new GenerateResult();
            backupSuffix = NormalizeSuffix(backupSuffix);
            var avatarRoot = rendererTransform != null ? FindAvatarRoot(rendererTransform) : null;
            int vc = baseMesh.vertexCount;

            var baked = new Dictionary<string, BakedShape>();
            var newNames = new List<string>();
            var masks = new Dictionary<float, float[]>();
            foreach (var d in definitions)
            {
                if (!IsValid(d, baseMesh)) continue;
                IEnumerable<BakedShape> shapes;
                if (d.kind == ShapeKind.Separator)
                {
                    shapes = new[] { EmptyShape(d.outputName.Trim(), vc) };
                }
                else
                {
                    float[] mask = null;
                    if (d.split != SideSplit.None && rendererTransform != null &&
                        !masks.TryGetValue(d.splitBlendWidth, out mask))
                        masks[d.splitBlendWidth] = mask =
                            LeftMask(baseMesh, rendererTransform, avatarRoot, d.splitBlendWidth);
                    shapes = Compute(baseMesh, d.outputName.Trim(), d.sources, d.split, mask);
                }

                foreach (var sh in shapes)
                {
                    if (baked.ContainsKey(sh.name)) res.warnings.Add(T("シェイプ名「{0}」が重複しています（後のものを使用）", sh.name));
                    else newNames.Add(sh.name);
                    baked[sh.name] = sh;
                }
            }

            res.backups.AddRange(PlanBackups(baseMesh, baked.Keys, backupSuffix));
            var backupOf = res.backups.ToDictionary(b => b.backup, b => b.original);
            res.order = PlanOrder(baseMesh, newNames, res.backups, backupHeader, order);

            var mesh = Object.Instantiate(baseMesh);
            mesh.name = baseMesh.name;
            var baseNames = Enumerable.Range(0, baseMesh.blendShapeCount).Select(baseMesh.GetBlendShapeName).ToList();

            // 既存シェイプの並びも中身も変わらず、末尾に足すだけなら既存シェイプは積み直さない（速い）
            bool appendOnly = res.backups.Count == 0 && res.order.Count >= baseNames.Count &&
                              res.order.Take(baseNames.Count).SequenceEqual(baseNames);
            if (!appendOnly) mesh.ClearBlendShapes();

            foreach (var name in appendOnly ? res.order.Skip(baseNames.Count) : res.order)
            {
                if (baked.TryGetValue(name, out var shape))
                {
                    Add(mesh, shape);
                }
                else if (backupOf.TryGetValue(name, out var original))
                {
                    foreach (var f in ReadFrames(baseMesh, baseMesh.GetBlendShapeIndex(original)))
                        mesh.AddBlendShapeFrame(name, f.weight, f.v, f.n, f.t);
                }
                else
                {
                    int idx = baseMesh.GetBlendShapeIndex(name);
                    if (idx >= 0)
                        foreach (var f in ReadFrames(baseMesh, idx))
                            mesh.AddBlendShapeFrame(name, f.weight, f.v, f.n, f.t);
                    else
                        Add(mesh, EmptyShape(name, vc)); // 自動で作る退避用の見出し
                }
            }

            res.mesh = mesh;
            return res;
        }

        /// <summary>置き換えになる元シェイプと退避先の名前（メッシュを作らずに名前だけ求める）</summary>
        public static List<(string original, string backup)> PlanBackups(Mesh baseMesh, IEnumerable<string> outputNames,
            string backupSuffix)
        {
            backupSuffix = NormalizeSuffix(backupSuffix);
            var outputs = new HashSet<string>(outputNames);
            var used = new HashSet<string>(outputs);
            for (int i = 0; i < baseMesh.blendShapeCount; i++) used.Add(baseMesh.GetBlendShapeName(i));

            var list = new List<(string, string)>();
            for (int i = 0; i < baseMesh.blendShapeCount; i++)
            {
                string name = baseMesh.GetBlendShapeName(i);
                if (!outputs.Contains(name)) continue;
                string backup = name + backupSuffix;
                for (int k = 2; used.Contains(backup); k++) backup = name + backupSuffix + k;
                used.Add(backup);
                list.Add((name, backup));
            }

            return list;
        }

        /// <summary>レシピの退避名一覧</summary>
        public static List<(string original, string backup)> PlanBackups(ShapeRecipe recipe)
        {
            return PlanBackups(recipe.baseMesh, OutputNamesOf(recipe.shapes, recipe.baseMesh), recipe.backupSuffix);
        }

        /// <summary>
        /// 元のシェイプを退避するとき、見た目が変わらないようウェイトを付け替える。
        /// 退避先がまだ無いメッシュからの切り替えなら、元の値を退避先へ移し、同名の新シェイプは 0 にする。
        /// </summary>
        public static Dictionary<string, float> BackupWeightOverrides(Mesh currentMesh, GenerateResult res,
            Func<string, float> weightOf)
        {
            var dict = new Dictionary<string, float>();
            foreach (var (original, backup) in res.backups)
            {
                if (currentMesh != null && currentMesh.GetBlendShapeIndex(backup) >= 0) continue;
                dict[backup] = weightOf(original);
                dict[original] = 0f;
            }

            return dict;
        }

        private static void Add(Mesh mesh, BakedShape s)
        {
            foreach (var (w, v, n, t) in s.frames) mesh.AddBlendShapeFrame(s.name, w, v, n, t);
        }

        /// <summary>メッシュを差し替え、ウェイトはシェイプ名で引き継ぐ</summary>
        public static void SwapMeshKeepingWeights(SkinnedMeshRenderer r, Mesh newMesh,
            Dictionary<string, float> overrides = null)
        {
            var weights = new Dictionary<string, float>();
            var old = r.sharedMesh;
            if (old != null)
                for (int i = 0; i < old.blendShapeCount; i++)
                    weights[old.GetBlendShapeName(i)] = r.GetBlendShapeWeight(i);
            if (overrides != null)
                foreach (var kv in overrides)
                    weights[kv.Key] = kv.Value;

            r.sharedMesh = newMesh;
            for (int i = 0; i < newMesh.blendShapeCount; i++)
                r.SetBlendShapeWeight(i, weights.TryGetValue(newMesh.GetBlendShapeName(i), out var w) ? w : 0f);
        }

        public static Transform FindAvatarRoot(Transform t)
        {
#if BSE_VRCSDK
            var d = t.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true);
            if (d != null) return d.transform;
#endif
            var animator = t.GetComponentInParent<Animator>(true);
            return animator != null ? animator.transform : t.root;
        }
    }
}
