using System;
using Content.Shared.MouseRotator;
using Robust.Client.Player;
using Robust.Shared.Timing;
using Robust.Shared.Maths;

namespace RobustoClient.Systems;

public class RobustaSpinSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    private float _lastDegrees;

    public override void FrameUpdate(float frameTime)
    {
        if (!_timing.IsFirstTimePredicted || !RobustaConfig.SpinBotEnabled)
            return;

        // Get local player
        var player = _player.LocalSession?.AttachedEntity;

        if (player == null || !TryComp<MouseRotatorComponent>(player.Value, out var rotator))
            return;

        _lastDegrees += RobustaConfig.SpinBotDegreesPerSecond * frameTime;
        _lastDegrees %= 360f;
        var angle = Angle.FromDegrees(_lastDegrees);
        var curRot = _transform.GetWorldRotation(Transform(player.Value));

        if (rotator.Simple4DirMode)
        {
            var angleDir = angle.GetCardinalDir();
            if (angleDir == curRot.GetCardinalDir())
                return;

            RaisePredictiveEvent(new RequestMouseRotatorRotationEvent()
            {
                Rotation = angle,
            });

            return;
        }

        var diff = Angle.ShortestDistance(angle, curRot);
        if (Math.Abs(diff.Theta) < rotator.AngleTolerance.Theta)
            return;

        // Send packet to server
        RaisePredictiveEvent(new RequestMouseRotatorRotationEvent
        {
            Rotation = angle
        });
    }
}