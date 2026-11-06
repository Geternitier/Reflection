namespace Reflection.Core;

// Exact critically damped motion: new wheel input changes the target without restarting velocity.
public sealed class ScrollDynamics
{
    public double Position { get; private set; }
    public double Target { get; private set; }
    public double Velocity { get; private set; }
    public bool Settled => Math.Abs(Target - Position) < .1 && Math.Abs(Velocity) < 2;
    public void Reset(double position) { Position = Target = position; Velocity = 0; }
    public void Add(double delta, double maximum)
    {
        if (delta * (Target - Position) < 0 || delta * Velocity < 0) { Target = Position; Velocity = 0; }
        Target = Math.Clamp(Target + delta, 0, Math.Max(0, maximum));
    }
    public void Shift(double delta, double maximum)
    {
        Position = Math.Clamp(Position + delta, 0, Math.Max(0, maximum));
        Target = Math.Clamp(Target + delta, 0, Math.Max(0, maximum));
    }
    public double Step(double seconds)
    {
        const double omega = 32;
        var dt = Math.Max(0, seconds); var displacement = Position - Target;
        var term = Velocity + omega * displacement; var decay = Math.Exp(-omega * dt);
        Position = Target + (displacement + term * dt) * decay;
        Velocity = (Velocity - omega * term * dt) * decay;
        if (Settled) { Position = Target; Velocity = 0; }
        return Position;
    }
}
