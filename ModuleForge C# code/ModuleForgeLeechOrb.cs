using System;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // The absorb orb: a bubble that leaves the enemy you hit and flies into your
    // ship. A DELIBERATE COPY of Weapon Forge's, same call as
    // ModuleForgeUnitTint and the buff HUD - a leech module has to be visible
    // with that mod absent, and the two are never live at once because Weapon
    // Forge owns the leech patch whenever it is installed.
    //
    // IT IS NOT A WEAPON AND NOT A PROJECTILE. It is a GameObject with a
    // SpriteRenderer and nothing else: no collider, no faction, no layer
    // politics, no way to hurt anything. A `Projectile` would arrive blank
    // (nothing on that class is serialized) and would then be visible to every
    // projectile patch in the mods.
    //
    // NOT duplicated from Weapon Forge: the optional trail on the orb. That
    // needs its particle-system grafting, which is a decoder rather than a
    // display layer - the same line the sprite and sound pipelines sit on.
    public class ModuleForgeLeechOrb : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge.Leech");

        [NonSerialized] private Transform _target;
        [NonSerialized] private Unit _player;
        [NonSerialized] private Resource _resource;
        [NonSerialized] private float _payload;
        [NonSerialized] private string _sfxGuid;

        [NonSerialized] private Vector3 _velocity;
        [NonSerialized] private float _speed;
        [NonSerialized] private float _spin;
        [NonSerialized] private float _dieAt;
        [NonSerialized] private bool _paid;
        [NonSerialized] private bool _counted;

        // A dead-straight line reads as a bullet being fired AT you, which is
        // the one thing this must not look like.
        private const float TurnRate = 6f;

        // The ship is about this wide; waiting for zero would park the orb
        // inside the hull for a frame.
        private const float ArriveWithin = 0.7f;

        // `payload` of 0 is normal: with healOn Hit the health has already gone
        // in and the orb is pure garnish.
        public static ModuleForgeLeechOrb Spawn(
            Vector3 from,
            Unit player,
            ModuleForgeLeech.Options options,
            Resource resource,
            float payload,
            Component victim)
        {
            if (player == null || options == null ||
                options.orbFrames == null || options.orbFrames.Length == 0)
            {
                return null;
            }

            try
            {
                var go = new GameObject("Forge Leech Orb");
                go.transform.position = from;
                go.transform.localScale =
                    Vector3.one * Mathf.Max(0.01f, options.orbScale);

                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = options.orbFrames[0];

                if (options.orbHasColor)
                    sr.color = options.orbColor;

                // A fresh SpriteRenderer lands on sorting layer "Default" at
                // order 0, which in this game is BEHIND the terrain - the orb
                // would be invisible for most of its flight. Borrow the layer
                // from something already drawing at the right depth, preferring
                // the thing we just hit (on screen by definition).
                CopySorting(sr, victim, player);

                if (options.orbFrames.Length > 1)
                {
                    var flip = go.AddComponent<Flipbook>();
                    flip.frames = options.orbFrames;
                    flip.fps = Mathf.Max(1f, options.orbFps);
                }

                var orb = go.AddComponent<ModuleForgeLeechOrb>();
                orb._target = player.transform;
                orb._player = player;
                orb._resource = resource;
                orb._payload = payload;
                orb._sfxGuid = options.sfxGuid;
                orb._speed = Mathf.Max(1f, options.orbSpeed);
                orb._spin = options.orbSpin;
                orb._dieAt = Time.time + Mathf.Max(0.2f, options.orbLifetime);
                orb._counted = true;

                Vector3 toShip = player.transform.position - from;

                if (toShip.sqrMagnitude < 0.0001f)
                    toShip = Vector3.up;

                Vector3 dir = toShip.normalized;
                Vector3 perp = new Vector3(-dir.y, dir.x, 0f);

                // Signed at random so a stream of orbs fans out rather than
                // tracing one line.
                float swoop = Mathf.Max(0f, options.orbArc) *
                              (UnityEngine.Random.value < 0.5f ? -1f : 1f);

                orb._velocity = (dir + perp * swoop).normalized * orb._speed;

                return orb;
            }
            catch (Exception e)
            {
                Log.LogWarning("Spawning the absorb orb failed: " + e);
                return null;
            }
        }

        private static void CopySorting(
            SpriteRenderer sr, Component victim, Unit player)
        {
            SpriteRenderer donor = null;

            if (victim != null)
                donor = victim.GetComponentInChildren<SpriteRenderer>(true);

            if (donor == null && player != null)
                donor = player.GetComponentInChildren<SpriteRenderer>(true);

            if (donor == null)
                return;

            sr.sortingLayerID = donor.sortingLayerID;
            sr.sortingOrder = donor.sortingOrder + 1;
        }

        private void Update()
        {
            if (_target == null || _player == null)
            {
                Destroy(gameObject);
                return;
            }

            float dt = Time.deltaTime;

            Vector3 toShip = _target.position - transform.position;
            float distance = toShip.magnitude;

            if (distance <= ArriveWithin || Time.time >= _dieAt)
            {
                Arrive();
                return;
            }

            // Steering rather than snapping, so the sideways kick reads as a
            // curve. RotateTowards keeps the speed constant and always
            // converges, so an orb can never orbit forever.
            Vector3 want = toShip / distance;

            _velocity = Vector3.RotateTowards(
                _velocity.normalized, want, TurnRate * dt, 0f) * _speed;

            transform.position += _velocity * dt;

            if (_spin != 0f)
                transform.Rotate(0f, 0f, _spin * dt);
        }

        // Pays out and dies. Also called on the lifetime backstop, because an
        // orb that cannot reach you is otherwise LOST HEALING - and healing that
        // silently fails to arrive is the worst way this could break.
        private void Arrive()
        {
            if (!_paid)
            {
                _paid = true;

                if (_payload > 0f)
                    ModuleForgeLeech.Heal(_player, _resource, _payload);

                if (!string.IsNullOrEmpty(_sfxGuid))
                {
                    try
                    {
                        AudioManager.PlaySfx(_sfxGuid, transform.position);
                    }
                    catch (Exception)
                    {
                        // A missing sound must never cost the healing.
                    }
                }
            }

            Destroy(gameObject);
        }

        // Every exit path lands here - arrival, the backstop, the ship dying, a
        // scene teardown - so the in-flight count cannot drift upward and
        // silently stop new orbs for the rest of the run.
        private void OnDestroy()
        {
            if (!_counted)
                return;

            _counted = false;
            ModuleForgeLeech.ReleaseOrb();
        }

        // A minimal sprite flipbook. Weapon Forge's ForgeSpriteAnimation does
        // more (ping-pong, play-once, a random start offset); an absorb orb
        // wants a plain loop, so this is the small honest version rather than a
        // copy of a bigger class.
        //
        // NOTE the frame list is assigned AFTER AddComponent, which runs Awake
        // synchronously - so nothing here may depend on `frames` in Awake. That
        // exact trap is what made Weapon Forge's `randomStart` silently never
        // work for years.
        public class Flipbook : MonoBehaviour
        {
            [NonSerialized] public Sprite[] frames;
            [NonSerialized] public float fps = 14f;

            private SpriteRenderer _renderer;
            private float _startTime;
            private int _last = -1;

            private void Awake()
            {
                _renderer = GetComponent<SpriteRenderer>();
                _startTime = Time.time;
            }

            private void Update()
            {
                if (_renderer == null || frames == null ||
                    frames.Length < 2 || fps <= 0f)
                {
                    return;
                }

                int i = Mathf.FloorToInt(
                    Mathf.Repeat((Time.time - _startTime) * fps,
                                 frames.Length));

                i = Mathf.Clamp(i, 0, frames.Length - 1);

                if (i == _last)
                    return;

                _last = i;

                if (frames[i] != null)
                    _renderer.sprite = frames[i];
            }
        }
    }
}
