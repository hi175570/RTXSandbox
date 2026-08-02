using ImGuiNET;
using RTXSandbox.Rendering;

namespace RTXSandbox;

/// <summary>Graphics settings section kept outside the game loop's main source file.</summary>
public sealed partial class SandboxGame
{
    private void DrawRendererSelector()
    {
        RenderBackendDescriptor active = RenderBackendCatalog.Get(_renderer.Kind);
        ImGui.Text($"渲染器：{active.ChineseDisplayName}");

        if (ImGui.BeginCombo("圖形模式", active.ChineseDisplayName))
        {
            foreach (RenderBackendDescriptor descriptor in RenderBackendCatalog.All)
            {
                if (!descriptor.Implemented) ImGui.BeginDisabled();
                bool selected = descriptor.Kind == _renderer.Kind;
                string label = descriptor.Implemented
                    ? descriptor.ChineseDisplayName
                    : $"{descriptor.ChineseDisplayName}（規劃中）";
                if (ImGui.Selectable(label, selected) && descriptor.Implemented)
                {
                    if (_renderer.Switch(descriptor.Kind))
                    {
                        _settings.Renderer = descriptor.Kind;
                        _renderer.ResetAccumulation();
                    }
                }
                if (selected) ImGui.SetItemDefaultFocus();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(descriptor.Description);
                if (!descriptor.Implemented) ImGui.EndDisabled();
            }
            ImGui.EndCombo();
        }

        string execution = _renderer.Capabilities.HasFlag(RenderBackendCapabilities.ProgressiveAccumulation)
            ? "漸進式累積"
            : "即時繪製";
        ImGui.SameLine();
        ImGui.TextDisabled(execution);
        ImGui.TextWrapped(_renderer.Status);
    }
}
