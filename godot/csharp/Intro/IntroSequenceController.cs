// Matthew W, 2026-08-12

using Godot;

namespace OpenDAO.Launcher;

[Tool]
public partial class IntroSequenceController : Control
{
    private const double UnavailableMovieSeconds = 1.35;

    private Control movieFallback = null!;
    private Label movieTitle = null!;
    private Label movieDetail = null!;
    private IntroSequencePlan plan = new([], []);
    private int movieIndex;
    private double elapsed;
    private State state;

    public event Action? SequenceCompleted;

    public event Action? MoviesStarting;

    private enum State
    {
        Idle,
        MovieFallback
    }

    public override void _Ready()
    {
        GetNode<TextureRect>("MovieSurface").Visible = false;
        GetNode<AudioStreamPlayer>("MovieAudio").Stop();
        movieFallback = GetNode<Control>("MovieFallback");
        movieTitle = GetNode<Label>("MovieFallback/Content/MovieTitle");
        movieDetail = GetNode<Label>("MovieFallback/Content/MovieDetail");
        SetProcess(false);
        SetProcessInput(false);
    }

    internal void Configure(IntroSequencePlan sequencePlan)
    {
        plan = sequencePlan ?? throw new ArgumentNullException(nameof(sequencePlan));
    }

    public void StartSequence()
    {
        movieIndex = 0;
        elapsed = 0;
        if (DisplayServer.GetName() == "headless")
        {
            CallDeferred(MethodName.Finish);
            return;
        }

        Visible = true;
        movieFallback.Visible = false;
        SetProcess(true);
        SetProcessInput(true);
        BeginMovies();
    }

    public override void _Process(double delta)
    {
        elapsed += delta;
        switch (state)
        {
            case State.MovieFallback when elapsed >= UnavailableMovieSeconds:
                AdvanceMovie();
                break;
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (state == State.Idle || !IsSkipEvent(@event))
        {
            return;
        }

        GetViewport().SetInputAsHandled();
        AdvanceMovie();
    }

    private void BeginMovies()
    {
        MoviesStarting?.Invoke();
        movieFallback.Visible = true;
        movieIndex = 0;
        StartCurrentMovie();
    }

    private void StartCurrentMovie()
    {
        if (movieIndex >= plan.Movies.Count)
        {
            Finish();
            return;
        }

        var movie = plan.Movies[movieIndex];
        movieTitle.Text = Path.GetFileNameWithoutExtension(movie.ResourceName)
            .Replace('_', ' ')
            .ToUpperInvariant();
        ShowMovieFallback();
    }

    private void ShowMovieFallback()
    {
        var movie = plan.Movies[movieIndex];
        movieFallback.Visible = true;
        const string detail = "Retail intro playback is intentionally excluded from the source-only runtime.";
        GD.PushWarning($"Intro playback skipped for {movie.ResourceName}: {detail}");
        movieDetail.Text = detail;
        elapsed = 0;
        state = State.MovieFallback;
    }

    private void AdvanceMovie()
    {
        movieIndex++;
        StartCurrentMovie();
    }

    private void Finish()
    {
        state = State.Idle;
        SetProcess(false);
        SetProcessInput(false);
        Visible = false;
        if (plan.Diagnostics.Count > 0)
        {
            GD.PushWarning("Intro sequence: " + string.Join(" | ", plan.Diagnostics));
        }

        SequenceCompleted?.Invoke();
    }

    private static bool IsSkipEvent(InputEvent @event) => @event switch
    {
        InputEventKey key => key.Pressed && !key.Echo,
        InputEventMouseButton mouse => mouse.Pressed,
        InputEventJoypadButton joypad => joypad.Pressed,
        _ => false
    };
}
