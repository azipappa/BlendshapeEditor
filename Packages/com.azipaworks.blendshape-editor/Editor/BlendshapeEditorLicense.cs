using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using static AzipaWorks.BlendshapeEditor.BseLocalization;

namespace AzipaWorks.BlendshapeEditor
{
    /*
     * Blendshape Editor のライセンス処理
     *
     * Blendshape Editor は有料のツールです。VPM パッケージは誰でもダウンロードできますが、
     * 利用できるのは Booth などで購入し、ライセンスインストーラーをインポートしたコンピュータだけです。
     *
     * このファイルのライセンス確認処理を改変・削除・回避すること、およびそれらを行ったものを配布することは、
     * 利用規約（パッケージに同梱の VN3 ライセンス PDF）で認めていません。
     */
    internal static class BlendshapeEditorLicense
    {
        /// <summary>Booth の商品ページ（未購入の画面のリンク先）。商品ページを作成したら設定する</summary>
        public const string BoothUrl = "";

        /// <summary>Booth で配布するライセンスインストーラーのファイル名（案内文に表示）</summary>
        public const string InstallerFileName = "BlendshapeEditor_License.unitypackage";

        private const string RegistryKey = @"Software\AzipaWorks";
        private const string AppKey = "blendshape-editor";
        private const string LicensedValue = "licensed";

        private static bool? _cache;

        /// <summary>このコンピュータにライセンスがインストールされているか（結果はキャッシュする）</summary>
        public static bool IsLicensed(bool refresh = false)
        {
            if (!refresh && _cache.HasValue) return _cache.Value;
            _cache = ReadRegistry() || ReadFile();
            return _cache.Value;
        }

        /// <summary>
        /// ライセンスファイルの場所。
        /// Windows: %APPDATA%\AzipaWorks、macOS: ~/Library/Application Support/AzipaWorks、Linux: ~/.local/share/AzipaWorks
        /// </summary>
        public static string LicenseFilePath()
        {
            string dir;
            if (Application.platform == RuntimePlatform.WindowsEditor)
                dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            else if (Application.platform == RuntimePlatform.OSXEditor)
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Application Support");
            else
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".local", "share");
            return Path.Combine(dir, "AzipaWorks", AppKey + ".lic");
        }

        private static bool ReadFile()
        {
            try
            {
                var path = LicenseFilePath();
                return File.Exists(path) && File.ReadAllText(path).Trim() == LicensedValue;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool ReadRegistry()
        {
#if UNITY_EDITOR_WIN
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryKey))
                    return key != null && (key.GetValue(AppKey) as string) == LicensedValue;
            }
            catch (Exception)
            {
                return false;
            }
#else
            return false;
#endif
        }

        [MenuItem("Tools/Azipa Tools/ライセンス管理/Blendshape Editor ライセンス削除", false, 915)]
        private static void Uninstall()
        {
            if (!IsLicensed(true))
            {
                EditorUtility.DisplayDialog("Blendshape Editor", T("このコンピュータには Blendshape Editor のライセンスがインストールされていません。"), "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog(T("Blendshape Editor ライセンス削除"),
                    T("このコンピュータから Blendshape Editor のライセンスを削除しますか？\n\n") +
                    T("もう一度使うには、ライセンスインストーラー（{0}）をインポートし直す必要があります。\n", InstallerFileName) +
                    T("作成したシェイプ（保存ファイル）は削除されず、アバターでそのまま使えます。"),
                    T("削除する"), T("キャンセル")))
                return;

            try
            {
#if UNITY_EDITOR_WIN
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryKey, true))
                    key?.DeleteValue(AppKey, false);
#endif
                var path = LicenseFilePath();
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            IsLicensed(true);
            EditorUtility.DisplayDialog("Blendshape Editor", T("ライセンスを削除しました。"), "OK");
        }
    }
}
