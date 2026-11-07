namespace SwipeMaze.Core;

/// <param name="X">Position, in the same units as the view the fireworks were started with.</param>
/// <param name="Size">Radius to draw it at.</param>
/// <param name="ColorIndex">0 to <see cref="Fireworks.ColorCount"/> - 1; every particle of one burst shares a colour.</param>
/// <param name="Alpha">1 when it appears, fading to 0 as it dies.</param>
public readonly record struct FireworkParticle(double X, double Y, double Size, int ColorIndex, double Alpha);

/// <summary>
/// The fireworks shown when a maze is solved: a handful of bursts, one after another, each a ring of
/// sparks that fly outwards, slow down, drop under gravity and fade. Pure arithmetic with no drawing,
/// so the app just feeds it the time that has passed and draws what it reports.
/// </summary>
public sealed class Fireworks
{
    public const int ColorCount = 6;

    private const int SparksPerBurst = 42;
    private const double Gravity = 110;     // units per second per second, pulling sparks down
    private const double Drag = 1.7;        // how quickly sparks lose their speed

    private sealed class Spark
    {
        public double X, Y, Vx, Vy, Age, Life, Size;
        public int Color;
    }

    private readonly Random _random;
    private readonly List<Spark> _sparks = new();
    private readonly List<double> _burstTimes = new();
    private double _width, _height, _time;

    public Fireworks(Random? random = null) => _random = random ?? new Random();

    /// <summary>True while a burst is still to come or any spark is still showing.</summary>
    public bool IsActive => _burstTimes.Count > 0 || _sparks.Count > 0;

    /// <summary>
    /// Starts a show in a view of this size: <paramref name="bursts"/> bursts spread over
    /// <paramref name="span"/> seconds, the first straight away.
    /// </summary>
    public void Start(double width, double height, int bursts = 7, double span = 2.0)
    {
        Clear();
        _width = width;
        _height = height;
        for (var i = 0; i < bursts; i++)
            _burstTimes.Add(i * span / bursts + (i == 0 ? 0 : _random.NextDouble() * 0.15));
    }

    /// <summary>Moves everything on by <paramref name="seconds"/>. Returns how many bursts went off during
    /// that time, so the caller can make a bang for each.</summary>
    public int Update(double seconds)
    {
        _time += seconds;

        var launched = 0;
        while (_burstTimes.Count > 0 && _burstTimes[0] <= _time)
        {
            _burstTimes.RemoveAt(0);
            Burst();
            launched++;
        }

        var slowing = Math.Exp(-Drag * seconds);
        foreach (var s in _sparks)
        {
            s.Age += seconds;
            s.Vx *= slowing;
            s.Vy = s.Vy * slowing + Gravity * seconds;
            s.X += s.Vx * seconds;
            s.Y += s.Vy * seconds;
        }
        _sparks.RemoveAll(s => s.Age >= s.Life);

        return launched;
    }

    /// <summary>What to draw right now.</summary>
    public IReadOnlyList<FireworkParticle> Snapshot() =>
        _sparks.Select(s =>
        {
            var fraction = s.Age / s.Life;
            return new FireworkParticle(s.X, s.Y, s.Size * (1 - 0.4 * fraction), s.Color, 1 - fraction);
        }).ToList();

    public void Clear()
    {
        _sparks.Clear();
        _burstTimes.Clear();
        _time = 0;
    }

    private void Burst()
    {
        // Upper part of the view, away from the edges, so the whole burst is visible.
        var cx = _width * (0.15 + 0.7 * _random.NextDouble());
        var cy = _height * (0.1 + 0.4 * _random.NextDouble());
        var color = _random.Next(ColorCount);
        var speed = 90 + _random.NextDouble() * 70;

        for (var i = 0; i < SparksPerBurst; i++)
        {
            var angle = 2 * Math.PI * (i + _random.NextDouble() * 0.5) / SparksPerBurst;
            var v = speed * (0.7 + 0.3 * _random.NextDouble());
            _sparks.Add(new Spark
            {
                X = cx,
                Y = cy,
                Vx = Math.Cos(angle) * v,
                Vy = Math.Sin(angle) * v,
                Life = 1.0 + _random.NextDouble() * 0.6,
                Size = 2.2 + _random.NextDouble() * 1.4,
                Color = color,
            });
        }
    }
}
