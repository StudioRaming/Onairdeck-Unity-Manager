using System;
using System.Collections.Generic;
using OnAirDeck.UnityManager.Theme;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The move of VCC-installed packages to the Manager (<see cref="VpmMigration"/>, owner decision 2026-10-31): a bar while VCC still
    /// lists a Manager-only package, then an undo bar until the move is undone or kept.
    /// </summary>
    internal sealed partial class ManagerWindow
    {
        [NonSerialized] private List<VpmHeldPackage> _vccHeld = new List<VpmHeldPackage>();
        [NonSerialized] private List<VpmMove> _vccMoves = new List<VpmMove>();

        private void RefreshVccState(string root)
        {
            _vccHeld = VpmMigration.Find(root);
            _vccMoves = VpmMigration.Pending(root);
        }

        /// <summary>The bar of a VCC install, or of the last move that can still be undone; 0 when there is none.</summary>
        private float DrawVccBar(Rect body)
        {
            if (_vccHeld.Count > 0)
            {
                var move = new BarButton { Label = ManagerText.VcMove, Kind = SRButtonKind.Primary, Enabled = !Busy, Tooltip = Busy ? ManagerText.WaitTip : "", Click = MoveFromVcc };
                return DrawBar(body, SRTone.Warning, ManagerText.VcBar.Replace("{p}", Names(_vccHeld.ConvertAll(h => h.Package))), move);
            }
            if (_vccMoves.Count > 0)
            {
                var last = _vccMoves[_vccMoves.Count - 1];
                var undo = new BarButton { Label = ManagerText.CfUndo, Kind = SRButtonKind.Secondary, Enabled = !Busy, Click = () => UndoVccMove(last) };
                var keep = new BarButton { Label = ManagerText.CfKeep, Kind = SRButtonKind.Ghost, Enabled = !Busy, Click = () => KeepVccMove(last) };
                return DrawBar(body, SRTone.Info, ManagerText.VcDoneBar.Replace("{p}", Names(last.Packages)), undo, keep);
            }
            return 0f;
        }

        private static string Names(List<string> packages)
        {
            return string.Join(", ", packages.ConvertAll(ManagerText.VcName).ToArray());
        }

        private void MoveFromVcc()
        {
            if (Busy) return;
            var root = PackageInstallService.ProjectRoot;
            // What VCC lists may have changed since the bar was drawn.
            var held = VpmMigration.Find(root);
            if (held.Count == 0) { RefreshInstalledState(); return; }
            var lines = held.ConvertAll(h => ManagerText.VcLine.Replace("{n}", ManagerText.VcName(h.Package)).Replace("{v}", h.FolderVersion)
                .Replace("{l}", h.ListedVersion.Length > 0 ? h.ListedVersion : "-"));
            var ask = ManagerText.VcAsk.Replace("{list}", string.Join("\n", lines.ToArray()));
            if (!EditorUtility.DisplayDialog(ManagerText.VcMove, ask, ManagerText.VcMove, ManagerText.Cancel)) return;
            try
            {
                VpmMigration.Move(root, held.ConvertAll(h => h.Package), DateTime.Now);
            }
            catch (PackageInstallException e)
            {
                SetMessage(e.Message, MessageType.Warning);
            }
            catch (Exception e)
            {
                SetMessage(e.Message, MessageType.Error);
                _messageReport = ManagerErrorWatch.OfferCaught("Moving a VCC install to the Manager", e);
            }
            finally
            {
                RefreshInstalledState();
            }
        }

        private void UndoVccMove(VpmMove move)
        {
            if (Busy) return;
            try
            {
                VpmMigration.Undo(PackageInstallService.ProjectRoot, move);
            }
            catch (PackageInstallException e)
            {
                SetMessage(e.Message, MessageType.Warning);
            }
            catch (Exception e)
            {
                SetMessage(e.Message, MessageType.Error);
                _messageReport = ManagerErrorWatch.OfferCaught("Undoing a move from VCC", e);
            }
            finally
            {
                RefreshInstalledState();
            }
        }

        private void KeepVccMove(VpmMove move)
        {
            try { VpmMigration.Keep(move); }
            catch (Exception e) { Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not keep the move from VCC: " + e.Message); }
            RefreshInstalledState();
        }
    }
}
