namespace OnAirDeck.UnityManager
{
    /// <summary>The move of VCC-installed packages to the Manager (VpmMigration). Placeholders in braces.</summary>
    internal static partial class ManagerText
    {
        /// <summary>{p} package names.</summary>
        public static string VcBar { get { return Pick("VCC로 설치한 패키지가 있어요: {p}. 이제 업데이트는 Atelier Manager로만 받아요. 옮겨 두면 VCC가 이 패키지를 예전 버전으로 되돌리지 않아요.", "Installed with VCC: {p}. Updates now come only through Atelier Manager. Once moved, VCC no longer puts an older version back.", "VCCで導入したパッケージがあります: {p}。今後の更新はAtelier Managerだけで受け取ります。移しておくと、VCCが古いバージョンに戻さなくなります。"); } }
        public static string VcMove { get { return Pick("Manager로 옮기기", "Move to the Manager", "Managerに移す"); } }
        /// <summary>{list} one line per package.</summary>
        public static string VcAsk { get { return Pick("{list}\n\nPackages/vpm-manifest.json에서 위 패키지 항목만 빼요. 패키지 폴더와 머티리얼, 프리셋 같은 프로젝트 파일은 그대로예요. 바꾸기 전 파일은 Library/OnAirDeck/Backup에 남기고, 언제든 되돌릴 수 있어요. 이후 업데이트는 Atelier Manager에서 받아요.", "{list}\n\nOnly these entries are taken out of Packages/vpm-manifest.json. The package folders and your project files (materials, presets) stay as they are. The file is kept in Library/OnAirDeck/Backup first, and you can undo this. Updates then come from Atelier Manager.", "{list}\n\nPackages/vpm-manifest.jsonから上のパッケージの項目だけを外します。パッケージのフォルダーやマテリアル、プリセットなどのプロジェクトファイルはそのままです。変更前のファイルはLibrary/OnAirDeck/Backupに残り、いつでも元に戻せます。以後の更新はAtelier Managerで受け取ります。"); } }
        /// <summary>{n} name, {v} installed version, {l} version VCC lists.</summary>
        public static string VcLine { get { return Pick("• {n} {v} (VCC 기록: {l})", "• {n} {v} (VCC lists {l})", "• {n} {v}（VCCの記録: {l}）"); } }
        /// <summary>{p} package names.</summary>
        public static string VcDoneBar { get { return Pick("{p}을(를) Manager로 옮겼어요. 이제 업데이트는 이 창에서 받아요.", "{p} moved to the Manager. Updates now come from this window.", "{p}をManagerに移しました。今後の更新はこのウィンドウで受け取ります。"); } }

        public static string VcName(string package)
        {
            switch (package)
            {
                case "com.studioraming.mingtoon": return "MingToon";
                case "com.studioraming.mingtoon.urp": return Pick("MingToon URP 애드온", "MingToon URP Add-on", "MingToon URP アドオン");
                default: return package ?? "";
            }
        }
    }
}
