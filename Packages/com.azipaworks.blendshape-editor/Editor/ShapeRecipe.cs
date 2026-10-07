using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AzipaWorks.BlendshapeEditor
{
    [Serializable]
    public class SourceShape
    {
        public string shapeName = "";

        /// <summary>新シェイプが 100 のときのこのシェイプの値（負の値で打ち消し）</summary>
        public float value = 100f;
    }

    public enum SideSplit
    {
        None,
        LeftOnly,
        RightOnly,
        Both,
    }

    public enum ShapeKind
    {
        /// <summary>元にするシェイプを合成して作る通常のシェイプ</summary>
        Normal,

        /// <summary>見出し用の空のシェイプ（例: ---- VRCHAT ----）。変形しない</summary>
        Separator,
    }

    [Serializable]
    public class ShapeDefinition
    {
        public ShapeKind kind = ShapeKind.Normal;
        public string outputName = "";
        public List<SourceShape> sources = new List<SourceShape>();
        public SideSplit split = SideSplit.None;
        public float splitBlendWidth = 0.004f;

        public ShapeDefinition Clone()
        {
            return new ShapeDefinition
            {
                kind = kind,
                outputName = outputName,
                sources = sources.Select(s => new SourceShape { shapeName = s.shapeName, value = s.value }).ToList(),
                split = split,
                splitBlendWidth = splitBlendWidth,
            };
        }
    }

    /// <summary>ある時点のシェイプキーの並び（番号 → 名前）</summary>
    [Serializable]
    public class OrderSnapshot
    {
        public int version;
        public List<string> names = new List<string>();
    }

    /// <summary>アバターの Avatar Descriptor が、どの時点の並びで瞬き・視線を設定しているか</summary>
    [Serializable]
    public class AvatarOrderRecord
    {
        /// <summary>Avatar Descriptor の GlobalObjectId</summary>
        public string id = "";

        public int version;
    }

    [Serializable]
    public class WeightEntry
    {
        public string name = "";
        public float value;
    }

    /// <summary>反映する直前のアバターの状態（「反映前に戻す」で使う）</summary>
    [Serializable]
    public class RestorePoint
    {
        /// <summary>SkinnedMeshRenderer の GlobalObjectId</summary>
        public string rendererId = "";

        /// <summary>表示用のアバター名（記録した時点）</summary>
        public string avatarName = "";

        /// <summary>反映前に使っていたメッシュ（FBX のメッシュなど）</summary>
        public Mesh mesh;

        /// <summary>反映前の各シェイプの値（名前 → 値）</summary>
        public List<WeightEntry> weights = new List<WeightEntry>();

        /// <summary>反映前の Avatar Descriptor の瞬き・視線の設定（このメッシュを使っていた場合）</summary>
        public bool hasEyelids;

        public string descriptorId = "";
        public int[] eyelids = Array.Empty<int>();

        /// <summary>すでに反映前に戻したか</summary>
        public bool restored;
    }

    /// <summary>1 回分の反映（いつ・どのアバターに反映したか）</summary>
    [Serializable]
    public class ReflectHistoryEntry
    {
        public string id = "";
        public long ticks;
        public List<RestorePoint> points = new List<RestorePoint>();

        public DateTime Time => new DateTime(ticks, DateTimeKind.Local);
    }

    /// <summary>
    /// 作成したメッシュ（.asset）に同梱する作成レシピ。
    /// 元メッシュと全シェイプの設定・並び順を持ち、いつでも元メッシュから作り直せるようにする。
    /// </summary>
    public class ShapeRecipe : ScriptableObject
    {
        public const string DefaultBackupHeader = "--- Blendshape Editor ---";

        public Mesh baseMesh;
        public string backupSuffix = "_orig";
        public List<ShapeDefinition> shapes = new List<ShapeDefinition>();

        /// <summary>完成したシェイプキーの並び順（名前）。空ならツールの既定の並び</summary>
        public List<string> order = new List<string>();

        /// <summary>退避したシェイプをまとめる見出し（区切り）の名前</summary>
        public string backupHeader = DefaultBackupHeader;

        // ---- Avatar Descriptor の付け直し用（並び順の履歴と、アバターごとの設定時点）
        public int orderVersion;
        public List<OrderSnapshot> orderHistory = new List<OrderSnapshot>();
        public List<AvatarOrderRecord> avatarRecords = new List<AvatarOrderRecord>();

        // ---- 反映の履歴（反映前に戻すための記録）
        public List<ReflectHistoryEntry> reflectHistory = new List<ReflectHistoryEntry>();

        private const int MaxReflectHistory = 30;

        public void AddReflect(ReflectHistoryEntry entry)
        {
            reflectHistory.Add(entry);
            if (reflectHistory.Count > MaxReflectHistory)
                reflectHistory.RemoveRange(0, reflectHistory.Count - MaxReflectHistory);
        }

        /// <summary>このレンダラーの、まだ戻していない一番新しい「反映前の記録」</summary>
        public RestorePoint LatestRestorePoint(string rendererId)
        {
            if (string.IsNullOrEmpty(rendererId)) return null;
            for (int i = reflectHistory.Count - 1; i >= 0; i--)
            {
                var p = reflectHistory[i].points.FirstOrDefault(x => x.rendererId == rendererId && !x.restored);
                if (p != null) return p;
            }

            return null;
        }

        private const int MaxHistory = 50;

        public OrderSnapshot Snapshot(int version) => orderHistory.FirstOrDefault(x => x.version == version);

        /// <summary>並びが変わっていれば新しい版として記録する。変わったら true</summary>
        public bool RecordOrder(IList<string> oldNames, IList<string> newNames)
        {
            if (orderHistory.Count == 0)
                orderHistory.Add(new OrderSnapshot { version = orderVersion, names = oldNames.ToList() });
            if (oldNames.SequenceEqual(newNames)) return false;
            orderVersion++;
            orderHistory.Add(new OrderSnapshot { version = orderVersion, names = newNames.ToList() });
            if (orderHistory.Count > MaxHistory) orderHistory.RemoveRange(0, orderHistory.Count - MaxHistory);
            return true;
        }

        public int? AvatarVersion(string id) => avatarRecords.FirstOrDefault(x => x.id == id)?.version;

        public void SetAvatarVersion(string id, int version)
        {
            if (string.IsNullOrEmpty(id)) return;
            var rec = avatarRecords.FirstOrDefault(x => x.id == id);
            if (rec == null) avatarRecords.Add(new AvatarOrderRecord { id = id, version = version });
            else rec.version = version;
        }

        public void RemoveAvatar(string id) => avatarRecords.RemoveAll(x => x.id == id);

        /// <summary>このツールで作成したメッシュならレシピを返す</summary>
        public static ShapeRecipe Find(Mesh mesh)
        {
            if (mesh == null) return null;
            var path = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) return null;
            return AssetDatabase.LoadAllAssetsAtPath(path).OfType<ShapeRecipe>().FirstOrDefault();
        }
    }
}
