namespace OnAirDeck.UnityManager
{
    /// <summary>The move of VCC-installed packages to the Manager (VpmMigration). Placeholders in braces.</summary>
    internal static partial class ManagerText
    {
        /// <summary>{p} package names.</summary>
        public static string VcBar { get { return Pick("VCC로 설치한 패키지가 있어요: {p}. 이제 업데이트는 Atelier Manager로만 받아요. 옮겨 두면 VCC가 이 패키지를 예전 버전으로 되돌리지 않아요.", "Installed with VCC: {p}. Updates now come only through Atelier Manager. Once moved, VCC no longer puts an older version back.", "VCCで導入したパッケージがあります: {p}。今後の更新はAtelier Managerだけで受け取ります。移しておくと、VCCが古いバージョンに戻さなくなります。"); } }
        public static string VcMove { get { return Pick("Manager로 옮기기", "Move to the Manager", "Managerに移す"); } }
        /// <summary>{list} one line per package.</summary>
        // Short: Unity's dialog on Windows clips a longer body.
        public static string VcAsk { get { return Pick("{list}\n\nvpm-manifest.json에서 이 항목만 빼요. 패키지와 머티리얼, 프리셋은 그대로예요. 원래 파일은 백업해 두니 되돌릴 수 있어요.", "{list}\n\nOnly these entries leave vpm-manifest.json. The package, materials and presets stay. The file is backed up, so you can undo this.", "{list}\n\nvpm-manifest.jsonからこの項目だけを外します。パッケージやマテリアル、プリセットはそのままです。元のファイルはバックアップするので元に戻せます。"); } }
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
