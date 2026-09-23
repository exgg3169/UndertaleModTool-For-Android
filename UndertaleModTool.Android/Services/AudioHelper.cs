using Android.Content;
using Android.Media;
using Stream = System.IO.Stream;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace UndertaleModTool.Android.Services;

/// <summary>
/// Finds, plays and replaces sound data.
/// </summary>
/// <remarks>
/// Sounds are either embedded in the data file (AUDO chunk), embedded in a separate
/// "audiogroupN.dat" file next to it, or stored as external .ogg files. Only the data file
/// itself is opened on Android, so audio group files have to be opened separately.
/// </remarks>
public static class AudioHelper
{
    /// <summary>Audio group files the user opened, by audio group ID.</summary>
    private static readonly Dictionary<int, (UndertaleData Data, global::Android.Net.Uri Uri)> AudioGroupFiles = new();

    private static MediaPlayer _player;

    static AudioHelper()
    {
        DataSession.DataChanged += () =>
        {
            lock (AudioGroupFiles)
            {
                foreach (var (data, _) in AudioGroupFiles.Values)
                    data.Dispose();
                AudioGroupFiles.Clear();
            }
            Stop();
        };
    }

    public static bool IsBuiltinGroup(UndertaleSound sound)
        => DataSession.Data is null || sound.GroupID == DataSession.Data.GetBuiltinSoundGroupID();

    public static bool IsExternal(UndertaleSound sound) => sound.AudioID < 0 && sound.AudioFile is null;

    /// <summary>Returns the audio group file that holds this sound, if it's been opened.</summary>
    public static UndertaleData GetGroupData(UndertaleSound sound)
    {
        lock (AudioGroupFiles)
            return AudioGroupFiles.TryGetValue(sound.GroupID, out var entry) ? entry.Data : null;
    }

    /// <summary>
    /// Returns the embedded audio entry of a sound, or null with an explanation in <paramref name="reason"/>.
    /// </summary>
    public static UndertaleEmbeddedAudio GetEmbeddedAudio(UndertaleSound sound, out string reason)
    {
        reason = null;
        if (IsExternal(sound))
        {
            reason = $"This sound is stored outside the data file, as \"{sound.File?.Content}\" in the game folder.";
            return null;
        }
        if (IsBuiltinGroup(sound))
        {
            if (sound.AudioFile is null)
                reason = "This sound has no audio data.";
            return sound.AudioFile;
        }

        UndertaleData group = GetGroupData(sound);
        if (group is null)
        {
            reason = $"This sound is stored in the audio group file \"{GroupFileName(sound.GroupID)}\", next to the data file. " +
                     "Use \"Open audio group file...\" to load it.";
            return null;
        }
        if (group.EmbeddedAudio is null || sound.AudioID < 0 || sound.AudioID >= group.EmbeddedAudio.Count)
        {
            reason = $"Audio ID {sound.AudioID} is not in {GroupFileName(sound.GroupID)}.";
            return null;
        }
        return group.EmbeddedAudio[sound.AudioID];
    }

    public static string GroupFileName(int groupId)
    {
        var groups = DataSession.Data?.AudioGroups;
        if (groups is not null && groupId >= 0 && groupId < groups.Count && groups[groupId]?.Path?.Content is { Length: > 0 } path)
            return path;
        return $"audiogroup{groupId}.dat";
    }

    /// <summary>Loads an audio group file for a group ID.</summary>
    public static int LoadGroupFile(Context context, global::Android.Net.Uri uri, int groupId)
    {
        UndertaleData data;
        using (Stream input = context.ContentResolver!.OpenInputStream(uri)!)
        using (MemoryStream ms = new())
        {
            input.CopyTo(ms);
            ms.Position = 0;
            data = UndertaleIO.Read(ms);
        }
        if (data.EmbeddedAudio is null)
        {
            data.Dispose();
            throw new InvalidDataException("This file has no embedded audio (AUDO chunk).");
        }
        lock (AudioGroupFiles)
        {
            if (AudioGroupFiles.Remove(groupId, out var old))
                old.Data.Dispose();
            AudioGroupFiles[groupId] = (data, uri);
        }
        return data.EmbeddedAudio.Count;
    }

    /// <summary>Writes a modified audio group file back to where it was opened from.</summary>
    public static void SaveGroupFile(Context context, int groupId)
    {
        (UndertaleData data, global::Android.Net.Uri uri) entry;
        lock (AudioGroupFiles)
        {
            if (!AudioGroupFiles.TryGetValue(groupId, out entry))
                return;
        }
        using MemoryStream ms = new();
        UndertaleIO.Write(ms, entry.data);
        using Stream output = context.ContentResolver!.OpenOutputStream(entry.uri, "wt")!;
        ms.Position = 0;
        ms.CopyTo(output);
    }

    public static string DetectExtension(byte[] data)
    {
        if (data.Length >= 4)
        {
            if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
                return ".wav";
            if (data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S')
                return ".ogg";
            if (data[0] == 'I' && data[1] == 'D' && data[2] == '3' || data[0] == 0xFF && (data[1] & 0xE0) == 0xE0)
                return ".mp3";
        }
        return ".bin";
    }

    /// <summary>Plays audio data; any previous playback is stopped.</summary>
    public static void Play(Context context, byte[] data, Action onCompleted = null)
    {
        Stop();
        string path = Path.Combine(context.CacheDir!.AbsolutePath, "preview" + DetectExtension(data));
        File.WriteAllBytes(path, data);

        MediaPlayer player = new();
        player.SetDataSource(path);
        player.Completion += (_, _) =>
        {
            Stop();
            onCompleted?.Invoke();
        };
        player.Prepare();
        player.Start();
        _player = player;
    }

    public static bool IsPlaying => _player is not null;

    public static void Stop()
    {
        MediaPlayer player = _player;
        _player = null;
        if (player is null)
            return;
        try
        {
            player.Stop();
        }
        catch
        {
            // Already stopped
        }
        player.Release();
    }

    /// <summary>Returns the duration in seconds of an audio file, or null if it can't be read.</summary>
    public static float? GetDurationSeconds(Context context, byte[] data)
    {
        string path = Path.Combine(context.CacheDir!.AbsolutePath, "probe" + DetectExtension(data));
        File.WriteAllBytes(path, data);
        try
        {
            using MediaMetadataRetriever retriever = new();
            retriever.SetDataSource(path);
            string ms = retriever.ExtractMetadata(MetadataKey.Duration);
            return long.TryParse(ms, out long value) ? value / 1000f : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Replaces a sound's audio with a WAV or OGG file, keeping its audio ID, and updates
    /// the sound's flags/type like UTMT's ImportSingleSound script.
    /// </summary>
    public static void ReplaceSoundAudio(Context context, UndertaleSound sound, byte[] newData)
    {
        string ext = DetectExtension(newData);
        if (ext is not (".wav" or ".ogg"))
            throw new InvalidDataException("Only WAV and OGG files can be used as GameMaker sounds.");

        UndertaleEmbeddedAudio audio = GetEmbeddedAudio(sound, out string reason)
            ?? throw new InvalidOperationException(reason);
        audio.Data = newData;

        var flags = UndertaleSound.AudioEntryFlags.Regular;
        if (ext == ".wav")
        {
            flags |= UndertaleSound.AudioEntryFlags.IsEmbedded;
        }
        else
        {
            // Keep "decode on load" if the sound had it; an embedded OGG is always compressed.
            flags |= UndertaleSound.AudioEntryFlags.IsCompressed;
            if (sound.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsEmbedded))
                flags |= UndertaleSound.AudioEntryFlags.IsEmbedded;
        }
        sound.Flags = flags;
        sound.Type = DataSession.Data.Strings.MakeString(ext);
        if (GetDurationSeconds(context, newData) is float seconds)
            sound.AudioLength = seconds;

        if (!IsBuiltinGroup(sound))
            SaveGroupFile(context, sound.GroupID);
        DataSession.IsModified = true;
    }
}
