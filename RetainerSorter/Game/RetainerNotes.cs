using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using RetainerSorter.Sorting;

namespace RetainerSorter.Game;

/// <summary>
/// Writes each retainer's comment (the note shown in the retainer list, "Edit retainer comment"
/// in the game) from what the retainer holds. The game keeps comments in its own module, which
/// saves them the same way as when the player types one.
/// </summary>
internal static unsafe class RetainerNotes
{
    // The game stores a comment in 91 bytes, including the terminator.
    private const int MaxBytes = 90;

    /// <summary>
    /// Brings the comments up to date if the option is on. Only retainers whose contents are known
    /// are touched, and only where the comment would change. Returns how many were rewritten.
    /// </summary>
    public static int Refresh()
    {
        if (!Plugin.Config.WriteNotes)
            return 0;

        try
        {
            var module = RetainerCommentModule.Instance();
            if (module == null)
                return 0;

            var world = Plugin.Reader.Read();
            var notes = Notes.Build(world, Plugin.Config.Active, Plugin.Catalog, Plugin.Catalog.ShortLabelFor);

            var changed = 0;
            for (var r = 0; r < notes.Length; r++)
            {
                if (notes[r] is not { } note || world.Retainers[r].Id == 0)
                    continue;

                note = Fit(note);
                var current = module->GetComment(world.Retainers[r].Id);
                var currentText = current.Value == null ? string.Empty : current.ToString();
                if (string.Equals(currentText, note, StringComparison.Ordinal))
                    continue;

                module->SetComment(world.Retainers[r].Id, note);
                changed++;
            }

            if (changed > 0)
            {
                // Make sure the game writes its comment file, as it does after the player edits one.
                module->HasChanges = true;
                Plugin.Log.Debug($"Rewrote the comments of {changed} retainers.");
            }

            return changed;
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Could not update the retainer comments.");
            return 0;
        }
    }

    /// <summary> Shortens a comment until it fits the game's storage, without cutting a character in half. </summary>
    private static string Fit(string note)
    {
        while (note.Length > 0 && Encoding.UTF8.GetByteCount(note) > MaxBytes)
            note = note[..^1];

        return note;
    }
}
