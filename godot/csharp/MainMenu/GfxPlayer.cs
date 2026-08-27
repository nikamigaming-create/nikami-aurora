// Matthew W, 2026-08-12

namespace OpenDAO.MainMenu;

internal sealed record GfxQuad(
    string Key,
    GfxExternalImage Image,
    GfxMatrix Fill,
    GfxMatrix Transform,
    float Alpha,
    int Blend);

internal sealed record GfxInstanceInfo(
    string Path,
    string Name,
    int CharacterId,
    GfxMatrix Transform);

internal sealed class GfxPlayer
{
    private const int MaxNesting = 12;
    private const int MaxInstances = 20000;
    private const int MaxQuads = 4096;
    private const int BlendMultiply = 3;

    private readonly GfxMovie movie;
    private readonly HashSet<ushort> settleAtEnd = [];
    private readonly Instance root = new() { CharacterId = -1 };
    private int visited;

    public GfxPlayer(GfxMovie movie)
    {
        this.movie = movie;
        foreach (var (characterId, timeline) in movie.Sprites)
        {
            if (timeline.Frames.Count < 2 || timeline.Actions.Count == 0 ||
                !timeline.Actions[0].Stop)
            {
                continue;
            }

            for (var frame = 1; frame < timeline.Frames.Count; frame++)
            {
                if (timeline.Frames[frame].Any(op => !op.Remove && op.CharacterId < 0))
                {
                    settleAtEnd.Add(characterId);
                    break;
                }
            }
        }
    }

    public void Advance()
    {
        visited = 0;
        Advance(root, 0, true);
    }

    public bool SeekRoot(string label)
    {
        if (!movie.Root.Labels.TryGetValue(label, out var frame) ||
            frame < 0 || frame >= movie.Root.Frames.Count)
        {
            return false;
        }

        root.Display.Clear();
        root.Frame = frame;
        root.Started = true;
        root.Stopped = true;
        root.JustJumped = false;
        for (var index = 0; index <= frame; index++)
        {
            ApplyFrame(root, movie.Root.Frames[index]);
        }

        InitializeChildren(root, 0);
        return true;
    }

    public List<GfxQuad> Collect()
    {
        var quads = new List<GfxQuad>();
        Collect(root, GfxMatrix.Identity, 1.0f, 0, 0, string.Empty, quads);
        return quads;
    }

    public GfxMatrix? FindInstance(string instanceName)
    {
        return FindInstance(root, GfxMatrix.Identity, instanceName, 0);
    }

    public List<GfxInstanceInfo> CollectInstances()
    {
        var instances = new List<GfxInstanceInfo>();
        CollectInstances(root, GfxMatrix.Identity, 0, string.Empty, instances);
        return instances;
    }

    private static void CollectInstances(
        Instance instance,
        GfxMatrix parent,
        int depth,
        string path,
        List<GfxInstanceInfo> instances)
    {
        if (depth > MaxNesting || instances.Count >= MaxInstances)
        {
            return;
        }

        foreach (var (slotDepth, slot) in instance.Display)
        {
            var slotPath = path.Length == 0 ? slotDepth.ToString() : $"{path}.{slotDepth}";
            var transform = slot.Matrix.Concat(parent);
            if (slot.Name.Length > 0)
            {
                instances.Add(new GfxInstanceInfo(
                    slotPath, slot.Name, slot.CharacterId, transform));
            }

            if (slot.Sprite is not null)
            {
                CollectInstances(slot.Sprite, transform, depth + 1, slotPath, instances);
            }
        }
    }

    private GfxMatrix? FindInstance(
        Instance instance,
        GfxMatrix parent,
        string instanceName,
        int depth)
    {
        if (depth > MaxNesting)
        {
            return null;
        }

        foreach (var slot in instance.Display.Values)
        {
            var transform = slot.Matrix.Concat(parent);
            if (string.Equals(slot.Name, instanceName, StringComparison.Ordinal))
            {
                return transform;
            }

            if (slot.Sprite is not null)
            {
                var found = FindInstance(slot.Sprite, transform, instanceName, depth + 1);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private GfxTimeline? TimelineFor(Instance instance)
    {
        if (instance.CharacterId < 0)
        {
            return movie.Root;
        }

        return movie.Sprites.TryGetValue((ushort)instance.CharacterId, out var timeline)
            ? timeline
            : null;
    }

    private void InitializeChildren(Instance instance, int depth)
    {
        if (depth >= MaxNesting)
        {
            return;
        }

        foreach (var slot in instance.Display.Values)
        {
            if (slot.Sprite is not { } child || TimelineFor(child) is not { Frames.Count: > 0 } timeline)
            {
                continue;
            }

            child.Display.Clear();
            child.Frame = 0;
            child.Started = true;
            child.Stopped = timeline.Actions.Count > 0 && timeline.Actions[0].Stop;
            child.JustJumped = false;
            ApplyFrame(child, timeline.Frames[0]);
            InitializeChildren(child, depth + 1);
        }
    }

    private void Advance(Instance instance, int depth, bool advanceSelf)
    {
        if (depth > MaxNesting || ++visited > MaxInstances)
        {
            return;
        }

        var timeline = TimelineFor(instance);
        if (timeline is null || timeline.Frames.Count == 0)
        {
            return;
        }

        var frameCount = timeline.Frames.Count;
        var tween = instance.CharacterId >= 0 && settleAtEnd.Contains((ushort)instance.CharacterId);
        if (tween && instance.Started && !instance.Stopped && instance.Frame == frameCount - 1)
        {
            instance.Stopped = true;
        }

        if (instance.JustJumped)
        {
            instance.JustJumped = false;
        }
        else if (instance.Started && advanceSelf && !instance.Stopped)
        {
            instance.Frame = (instance.Frame + 1) % frameCount;
        }

        instance.Started = true;
        ApplyFrame(instance, timeline.Frames[instance.Frame]);

        if (instance.Frame < timeline.Actions.Count)
        {
            var action = timeline.Actions[instance.Frame];
            if (action.Stop && !(tween && instance.Frame == 0))
            {
                instance.Stopped = true;
            }

            if (action.Play)
            {
                instance.Stopped = false;
            }

            var target = -1;
            if (action.GotoLabel.Length > 0)
            {
                if (timeline.Labels.TryGetValue(action.GotoLabel, out var labelled))
                {
                    target = labelled;
                }
            }
            else if (action.GotoFrame >= 0)
            {
                target = action.GotoFrame;
            }

            if (target >= 0 && target < frameCount)
            {
                instance.Display.Clear();
                for (var frame = 0; frame <= target; frame++)
                {
                    ApplyFrame(instance, timeline.Frames[frame]);
                }

                instance.Frame = target;
                instance.JustJumped = true;
                if (!action.Stop)
                {
                    instance.Stopped = false;
                }
            }
        }

        foreach (var slot in instance.Display.Values)
        {
            if (slot.Sprite is not null)
            {
                Advance(slot.Sprite, depth + 1, true);
            }
        }
    }

    private void ApplyFrame(Instance instance, List<GfxOp> ops)
    {
        foreach (var op in ops)
        {
            if (op.Remove)
            {
                instance.Display.Remove(op.Depth);
                continue;
            }

            if (!instance.Display.TryGetValue(op.Depth, out var slot))
            {
                slot = new Slot();
                instance.Display[op.Depth] = slot;
            }

            if (op.CharacterId >= 0 && op.CharacterId != slot.CharacterId)
            {
                slot.CharacterId = op.CharacterId;
                slot.Sprite = null;
                if (movie.Sprites.ContainsKey((ushort)op.CharacterId))
                {
                    slot.Sprite = new Instance { CharacterId = op.CharacterId };
                }
            }

            if (op.Matrix is not null)
            {
                slot.Matrix = op.Matrix.Value;
            }

            if (op.Alpha is not null)
            {
                slot.Alpha = op.Alpha.Value;
            }

            if (op.Blend is not null)
            {
                slot.Blend = op.Blend.Value;
            }

            if (op.Ratio is not null)
            {
                slot.Ratio = op.Ratio.Value;
            }

            if (op.ClipDepth != 0)
            {
                slot.ClipDepth = op.ClipDepth;
            }

            if (op.Name is not null)
            {
                slot.Name = op.Name;
            }
        }
    }

    private void Collect(
        Instance instance,
        GfxMatrix parent,
        float parentAlpha,
        int parentBlend,
        int depth,
        string path,
        List<GfxQuad> quads)
    {
        if (depth > MaxNesting || quads.Count >= MaxQuads)
        {
            return;
        }

        foreach (var (slotDepth, slot) in instance.Display)
        {
            if (slot.ClipDepth != 0)
            {
                continue;
            }

            var slotPath = path.Length == 0 ? slotDepth.ToString() : $"{path}.{slotDepth}";
            var transform = slot.Matrix.Concat(parent);
            var alpha = slot.Alpha * parentAlpha;
            var blend = slot.Blend != 0 ? slot.Blend : parentBlend;
            if (slot.Sprite is not null)
            {
                Collect(slot.Sprite, transform, alpha, blend, depth + 1, slotPath, quads);
                continue;
            }

            if (alpha <= 0.002f)
            {
                continue;
            }

            if (!movie.Shapes.TryGetValue((ushort)slot.CharacterId, out var shape))
            {
                continue;
            }

            var fillIndex = 0;
            foreach (var fill in shape.Fills)
            {
                if (movie.Images.TryGetValue(fill.ImageCharacterId, out var image))
                {
                    quads.Add(new GfxQuad(
                        $"{slotPath}#{fillIndex}", image, fill.Matrix, transform, alpha, blend));
                }

                fillIndex++;
            }
        }
    }

    private sealed class Instance
    {
        public int CharacterId { get; init; } = -1;

        public int Frame { get; set; }

        public bool Started { get; set; }

        public bool Stopped { get; set; }

        public bool JustJumped { get; set; }

        public SortedDictionary<ushort, Slot> Display { get; } = [];
    }

    private sealed class Slot
    {
        public int CharacterId { get; set; } = -1;

        public GfxMatrix Matrix { get; set; } = GfxMatrix.Identity;

        public float Alpha { get; set; } = 1.0f;

        public int Blend { get; set; }

        public float Ratio { get; set; }

        public ushort ClipDepth { get; set; }

        public string Name { get; set; } = string.Empty;

        public Instance? Sprite { get; set; }
    }
}
