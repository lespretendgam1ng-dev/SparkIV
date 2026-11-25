/**********************************************************************\

 SparkIV
 Copyright (C) 2026  reditec

 This program is free software: you can redistribute it and/or modify
 it under the terms of the GNU General Public License as published by
 the Free Software Foundation, either version 3 of the License, or
 (at your option) any later version.

 This program is distributed in the hope that it will be useful,
 but WITHOUT ANY WARRANTY; without even the implied warranty of
 MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 GNU General Public License for more details.

 You should have received a copy of the GNU General Public License
 along with this program.  If not, see <http://www.gnu.org/licenses/>.

\**********************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RageLib.GtaVI.Shaders;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// A GTA VI shader database (SGD5): its shaders by stage, with name, shader model, size and hash, exported as
  /// DXBC/DXIL containers; and its effects with their techniques and the shaders of each pass.
  /// </summary>
  public sealed partial class ShaderDatabaseView : UserControl
  {
    private readonly ViewerContext _context;
    private readonly Sgd5Database _database;
    private readonly List<ShaderRow> _rows;
    private string _sortKey = "";
    private bool _ascending = true;

    internal ShaderDatabaseView(ViewerContext context, Sgd5Database database)
    {
      InitializeComponent();
      _context = context;
      _database = database;
      _rows = database.Shaders.Select(s => new ShaderRow(s)).ToList();

      StageBox.Items.Add("All stages");
      foreach (var stage in database.Shaders.Select(s => s.Stage).Distinct().OrderBy(s => s))
      {
        StageBox.Items.Add(Sgd5Database.Stages[stage]);
      }
      StageBox.SelectedIndex = 0;

      EffectList.ItemsSource = database.Effects.Select(e => new EffectRow(e)).ToList();
      EffectsTab.Header = "Effects (" + database.Effects.Count.ToString("N0") + ")";
      ShowRows();
      if (database.Effects.Count > 0)
      {
        EffectList.SelectedIndex = 0;
      }
    }

    private void ShowRows()
    {
      string filter = FilterBox.Text.Trim();
      string? stage = StageBox.SelectedIndex > 0 ? StageBox.SelectedItem as string : null;
      IEnumerable<ShaderRow> rows = _rows;
      if (stage != null) rows = rows.Where(r => r.Stage == stage);
      if (filter.Length > 0) rows = rows.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
      var shown = rows.ToList();
      ShaderList.ItemsSource = shown;
      ShadersTab.Header = "Shaders (" + _rows.Count.ToString("N0") + ")";
      InfoText.Text = shown.Count.ToString("N0") + " of " + _rows.Count.ToString("N0") + " shaders   ·   " +
                      string.Join(", ", _rows.GroupBy(r => r.Stage).Select(g => g.Count().ToString("N0") + " " + g.Key)) +
                      "   ·   " + _database.Effects.Count.ToString("N0") + " effects   ·   DXBC containers with DXIL" +
                      (_database.FromTables ? "" : "   ·   tables not read: shaders found by their marker, stages guessed from the names");
    }

    private void Filter_Changed(object sender, object e)
    {
      if (_rows != null)
      {
        ShowRows();
      }
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
      string key = (string)((Button)sender).Tag;
      _ascending = key != _sortKey || !_ascending;
      _sortKey = key;
      Func<ShaderRow, IComparable> by = key switch
      {
        "Stage" => r => r.Shader.Stage,
        "Model" => r => r.Shader.Kind,
        "Size" => r => r.Shader.UnpackedSize,
        "Hash" => r => r.Shader.Hash,
        _ => r => r.Name,
      };
      var sorted = (_ascending ? _rows.OrderBy(by) : _rows.OrderByDescending(by)).ToList();
      _rows.Clear();
      _rows.AddRange(sorted);
      ShowRows();
    }

    private void ShaderList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
      ExportSelectedButton.IsEnabled = ShaderList.SelectedItems.Count > 0;

    private async void ShaderList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
      if (ShaderList.SelectedItem is ShaderRow row)
      {
        await ExportAsync(new[] { row.Shader });
      }
    }

    private async void ExportSelected_Click(object sender, RoutedEventArgs e) =>
      await ExportAsync(ShaderList.SelectedItems.OfType<ShaderRow>().Select(r => r.Shader).ToList());

    private async Task ExportAsync(IReadOnlyList<Sgd5Shader> shaders)
    {
      if (shaders.Count == 0)
      {
        return;
      }
      if (shaders.Count == 1)
      {
        var shader = shaders[0];
        string? path = await ViewerPages.PickSaveFileAsync(_context.Window, SafeName(shader.Name) + ".dxbc", ".dxbc");
        if (path == null)
        {
          return;
        }
        try
        {
          byte[] data = shader.Unpack() ?? throw new InvalidDataException("The shader's packed data is corrupt.");
          await System.IO.File.WriteAllBytesAsync(path, data);
          _context.ShowMessage("Exported", path + "   ·   DXBC container with DXIL (open it with dxc -dumpbin)", false);
        }
        catch (Exception ex)
        {
          _context.ShowMessage("Could not export " + shader.Name, ex.Message, true);
        }
        return;
      }

      string? folder = await PickFolderAsync();
      if (folder != null)
      {
        await RunAsync(() => WriteShaders(shaders, folder, byStage: false), folder, shaders.Count, effects: false);
      }
    }

    private async void ExportAll_Click(object sender, RoutedEventArgs e)
    {
      string? folder = await PickFolderAsync();
      if (folder == null)
      {
        return;
      }
      await RunAsync(() =>
      {
        int failed = WriteShaders(_database.Shaders, folder, byStage: true);
        if (_database.Effects.Count > 0)
        {
          System.IO.File.WriteAllText(Path.Combine(folder, "effects.txt"), EffectsText(_database.Effects), new UTF8Encoding(false));
        }
        return failed;
      }, folder, _database.Shaders.Count, effects: _database.Effects.Count > 0);
    }

    private async Task RunAsync(Func<int> write, string folder, int count, bool effects)
    {
      Working.IsActive = true;
      try
      {
        int failed = await Task.Run(write);
        _context.ShowMessage("Exported",
          (count - failed).ToString("N0") + " shaders saved to " + folder + (effects ? " (one folder per stage), with effects.txt" : "") + "." +
          (failed > 0 ? " " + failed + " could not be unpacked." : ""), failed > 0);
      }
      catch (Exception ex)
      {
        _context.ShowMessage("Could not export the shaders", ex.Message, true);
      }
      finally
      {
        Working.IsActive = false;
      }
    }

    /// <summary>Writes the shaders as .dxbc files; returns how many could not be unpacked.</summary>
    private static int WriteShaders(IEnumerable<Sgd5Shader> shaders, string folder, bool byStage)
    {
      int failed = 0;
      var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var shader in shaders)
      {
        string dir = byStage ? Path.Combine(folder, shader.StageName) : folder;
        System.IO.Directory.CreateDirectory(dir);
        string name = SafeName(shader.Name);
        string path = Path.Combine(dir, name + ".dxbc");
        for (int i = 2; !used.Add(path); i++)
        {
          path = Path.Combine(dir, name + "~" + i + ".dxbc");
        }
        byte[]? data = shader.Unpack();
        if (data == null)
        {
          failed++;
          continue;
        }
        System.IO.File.WriteAllBytes(path, data);
      }
      return failed;
    }

    private async Task<string?> PickFolderAsync()
    {
      var picker = new Windows.Storage.Pickers.FolderPicker();
      picker.FileTypeFilter.Add("*");
      WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_context.Window));
      var folder = await picker.PickSingleFolderAsync();
      return folder?.Path;
    }

    private async void ExportOriginal_Click(object sender, RoutedEventArgs e)
    {
      string name = _context.File.Name;
      string? path = await ViewerPages.PickSaveFileAsync(_context.Window, name, Path.GetExtension(name));
      if (path != null)
      {
        await System.IO.File.WriteAllBytesAsync(path, _context.Data);
        _context.ShowMessage("Exported", path, false);
      }
    }

    private void EffectList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
      EffectText.Text = EffectList.SelectedItem is EffectRow row ? EffectsText(new[] { row.Effect }) : "";

    /// <summary>The effects as text: per technique, its passes with the shader of each stage and the state they set.</summary>
    private static string EffectsText(IEnumerable<Sgd5Effect> effects)
    {
      var text = new StringBuilder();
      foreach (var effect in effects)
      {
        text.Append("effect ").Append(effect.Name).Append("   (").Append(effect.Techniques.Count).Append(" techniques; states: ")
            .Append(effect.RasterizerStates).Append(" rasterizer, ").Append(effect.DepthStencilStates).Append(" depth-stencil, ")
            .Append(effect.BlendStates).AppendLine(" blend)");
        foreach (var technique in effect.Techniques)
        {
          text.Append("  technique ").AppendLine(technique.Name);
          for (int p = 0; p < technique.Passes.Count; p++)
          {
            var pass = technique.Passes[p];
            text.Append("    pass ").Append(p + 1).Append(':');
            for (int slot = 0; slot < pass.Shaders.Length; slot++)
            {
              if (pass.Shaders[slot] is Sgd5Shader shader)
              {
                text.Append(' ').Append(Sgd5Database.Stages[slot]).Append(' ').Append(shader.Name).Append(';');
              }
            }
            var sets = new List<string>();
            if (pass.SetsRasterizer) sets.Add("rasterizer");
            if (pass.SetsDepthStencil) sets.Add("depth-stencil");
            if (pass.SetsBlend) sets.Add("blend");
            if (pass.StencilRef is byte r) sets.Add("stencil ref 0x" + r.ToString("X2"));
            if (sets.Count > 0) text.Append(" sets ").Append(string.Join(", ", sets));
            text.AppendLine();
          }
        }
        text.AppendLine();
      }
      return text.ToString();
    }

    private static string SafeName(string name)
    {
      foreach (char c in Path.GetInvalidFileNameChars())
      {
        name = name.Replace(c, '_');
      }
      return name;
    }
  }

  /// <summary>One row of the shader list.</summary>
  public sealed class ShaderRow
  {
    internal ShaderRow(Sgd5Shader shader) => Shader = shader;

    internal Sgd5Shader Shader { get; }
    public string Name => Shader.Name;
    public string Stage => Shader.StageName;
    public string Model => Shader.ModelName;
    public string SizeText => Shader.UnpackedSize.ToString("N0") + " B";
    public string HashText => Shader.Hash == 0 ? "–" : Shader.Hash.ToString("X16");
  }

  /// <summary>One row of the effect list.</summary>
  public sealed class EffectRow
  {
    internal EffectRow(Sgd5Effect effect) => Effect = effect;

    internal Sgd5Effect Effect { get; }
    public string Name => Effect.Name;
    public string Detail => Effect.Techniques.Count == 1 ? "1 technique" : Effect.Techniques.Count.ToString("N0") + " techniques";
  }

  /// <summary>GTA VI shader databases (SGD5).</summary>
  internal sealed class ShaderDatabaseViewer : IViewer
  {
    public object Load(RageLib.FileSystem.Common.File file, byte[] data) => Sgd5Database.Open(data);

    public FrameworkElement CreateView(ViewerContext context, object loaded) => new ShaderDatabaseView(context, (Sgd5Database)loaded);
  }
}
