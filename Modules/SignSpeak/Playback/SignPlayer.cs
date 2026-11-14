using System;
using System.Collections.Generic;
using SignSpeak.Signs;

namespace SignSpeak.Playback;

public enum PlayerState
{
    /// <summary>Nothing queued; the character is entirely under its own animation.</summary>
    Idle,

    /// <summary>Working through the queue.</summary>
    Signing,

    /// <summary>Queue finished; easing back to rest before letting go.</summary>
    Releasing,
}

/// <summary>
/// Walks a queued message one handshape at a time.
///
/// The player owns time, not bones. It never touches a pose: it reports which
/// key it is heading for, how far through that move it is, and bumps
/// <see cref="TransitionEpoch"/> whenever a new move begins. The host snapshots
/// its own last emitted pose at each epoch change and blends from there, which
/// is what makes an interruption mid-letter continue smoothly instead of
/// snapping back to the previous shape.
///
/// Keeping poses out of here is also what lets a hand-authored letter and a
/// procedural one sit next to each other in the same message with no special
/// casing.
/// </summary>
public sealed class SignPlayer
{
    /// <summary>Seconds spent easing the arm up into signing position.</summary>
    private const float RaiseSeconds = 0.45f;

    /// <summary>Seconds spent easing back to rest at the end of a message.</summary>
    private const float ReleaseSeconds = 0.55f;

    /// <summary>Sentinel target meaning "the character's own animation, untouched".</summary>
    public const char Rest = '\0';

    private readonly SignLibrary _library;
    private readonly Queue<SignStep> _queue = new();

    private SignStep _step;
    private float _phaseTime;
    private bool _inHold;
    private float _gapRemaining;
    private float _raiseTime;
    private float _releaseTime;

    public SignPlayer(SignLibrary library) => _library = library;

    public PlayerState State { get; private set; } = PlayerState.Idle;

    /// <summary>The character being moved toward, or <see cref="Rest"/>.</summary>
    public char TargetCharacter { get; private set; } = Rest;

    /// <summary>Which key of that character, for the multi-key motion letters.</summary>
    public int TargetKeyIndex { get; private set; }

    /// <summary>Eased 0..1 progress from the snapshot toward the target.</summary>
    public float TransitionProgress { get; private set; }

    /// <summary>Increments whenever a new move starts, telling the host to re-snapshot.</summary>
    public int TransitionEpoch { get; private set; }

    /// <summary>
    /// How much of the pose to apply overall, 0..1. Ramps in over
    /// <see cref="RaiseSeconds"/> so the arm lifts at a natural rate even when
    /// the first letter's own transition is much quicker than that.
    /// </summary>
    public float Engage { get; private set; }

    /// <summary>Playback rate multiplier. Higher is faster.</summary>
    public float Speed { get; set; } = 1f;

    /// <summary>Set once when a message finishes, telling the host to drop the profile.</summary>
    public bool WantsRelease { get; private set; }

    public int Remaining => _queue.Count + (State == PlayerState.Signing ? 1 : 0);

    public void Play(IEnumerable<SignStep> steps)
    {
        _queue.Clear();
        foreach (var step in steps)
            _queue.Enqueue(step);

        if (_queue.Count == 0)
        {
            Stop();
            return;
        }

        _gapRemaining = 0f;
        _releaseTime = 0f;
        WantsRelease = false;
        State = PlayerState.Signing;

        _step = _queue.Dequeue();
        BeginKey(_step.Source, 0);
    }

    /// <summary>Abandons the queue and eases back to rest.</summary>
    public void Stop()
    {
        _queue.Clear();

        if (State == PlayerState.Idle)
            return;

        BeginRelease();
    }

    /// <summary>Drops everything immediately, with no easing. Used on unload.</summary>
    public void Reset()
    {
        _queue.Clear();
        State = PlayerState.Idle;
        TargetCharacter = Rest;
        TargetKeyIndex = 0;
        TransitionProgress = 0f;
        Engage = 0f;
        _raiseTime = 0f;
        _releaseTime = 0f;
        _gapRemaining = 0f;
        _phaseTime = 0f;
        _inHold = false;
        WantsRelease = false;
        TransitionEpoch++;
    }

    public void Advance(float deltaSeconds)
    {
        var speed = Math.Max(0.1f, Speed);

        switch (State)
        {
            case PlayerState.Idle:
                return;

            case PlayerState.Releasing:
                AdvanceRelease(deltaSeconds);
                return;

            case PlayerState.Signing:
                AdvanceSigning(deltaSeconds * speed, deltaSeconds);
                return;
        }
    }

    private void AdvanceRelease(float deltaSeconds)
    {
        _releaseTime += deltaSeconds;
        TransitionProgress = Ease(Math.Clamp(_releaseTime / ReleaseSeconds, 0f, 1f));

        if (_releaseTime < ReleaseSeconds)
            return;

        State = PlayerState.Idle;
        Engage = 0f;
        TransitionProgress = 1f;
        _raiseTime = 0f;
        WantsRelease = true;
    }

    private void AdvanceSigning(float scaledDelta, float realDelta)
    {
        // The raise runs in real time, not scaled by playback speed, so a fast
        // message still lifts the arm at a believable rate.
        _raiseTime = Math.Min(_raiseTime + realDelta, RaiseSeconds);
        Engage = Ease(_raiseTime / RaiseSeconds);

        if (_gapRemaining > 0f)
        {
            _gapRemaining -= scaledDelta;
            TransitionProgress = 1f;
            return;
        }

        _phaseTime += scaledDelta;

        var (transition, hold) = _library.Timing(TargetCharacter, TargetKeyIndex);

        if (!_inHold)
        {
            var duration = Math.Max(0.01f, transition);
            if (_phaseTime < duration)
            {
                TransitionProgress = Ease(_phaseTime / duration);
                return;
            }

            TransitionProgress = 1f;
            _inHold = true;
            _phaseTime -= duration;
        }

        if (_phaseTime < Math.Max(0.01f, hold))
        {
            TransitionProgress = 1f;
            return;
        }

        // This key is finished. Move to the next key of the same character, or
        // on to the next character in the queue.
        var nextKey = TargetKeyIndex + 1;
        if (nextKey < _library.KeyCount(TargetCharacter))
        {
            BeginKey(TargetCharacter, nextKey);
            return;
        }

        _gapRemaining = _step.GapSeconds;

        if (_queue.Count > 0)
        {
            _step = _queue.Dequeue();
            BeginKey(_step.Source, 0);
            return;
        }

        BeginRelease();
    }

    private void BeginKey(char character, int keyIndex)
    {
        TargetCharacter = character;
        TargetKeyIndex = keyIndex;
        TransitionProgress = 0f;
        _phaseTime = 0f;
        _inHold = false;
        TransitionEpoch++;
    }

    private void BeginRelease()
    {
        State = PlayerState.Releasing;
        TargetCharacter = Rest;
        TargetKeyIndex = 0;
        TransitionProgress = 0f;
        _releaseTime = 0f;
        _gapRemaining = 0f;
        TransitionEpoch++;
    }

    /// <summary>Acknowledges the release signal so the host only clears once.</summary>
    public bool ConsumeRelease()
    {
        if (!WantsRelease)
            return false;

        WantsRelease = false;
        return true;
    }

    /// <summary>Smoothstep. Real signing accelerates out of one shape and settles into the next.</summary>
    private static float Ease(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
