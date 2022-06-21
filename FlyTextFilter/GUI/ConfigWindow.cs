﻿using System.Numerics;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using ImGuiNET;

namespace FlyTextFilter.GUI;

internal class ConfigWindow : Window
{
    public bool HasPassedTests;
    private readonly TypesTab.TypesTab typesTab = new();
    private readonly BlacklistTab blacklistTab = new();
    private string message = string.Empty;
    private Vector4 messageColor = ImGuiColors.DalamudWhite;

    public ConfigWindow()
        : base("FlyTextFilter")
    {
        this.RespectCloseHotkey = true;
        this.Flags = ImGuiWindowFlags.AlwaysAutoResize;

        FlyTextKindTests.RunTests(out this.HasPassedTests);
    }

    public override void OnOpen()
    {
        this.UpdateMessage(string.Empty, ImGuiColors.DalamudWhite);
        base.OnOpen();
    }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("##ConfigTab"))
        {
            if (ImGui.BeginTabItem("Types"))
            {
                this.typesTab.Draw();

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Log"))
            {
                LogTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Text Blacklist"))
            {
                this.blacklistTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Positions"))
            {
                PositionsTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Misc"))
            {
                MiscTab.Draw();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    public void UpdateMessage(string msg, Vector4 color)
    {
        this.message = msg;
        this.messageColor = color;
    }

    public void DrawMessage()
    {
        if (this.message != string.Empty)
        {
            ImGui.SameLine();
            ImGui.TextColored(this.messageColor, this.message);
        }
    }
}
