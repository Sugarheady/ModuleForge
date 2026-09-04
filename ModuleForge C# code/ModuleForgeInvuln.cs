using System;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // Mercy invincibility: a window of TOTAL immunity after taking a hit.
    //
    // This is the game's own switch, not a mod-invented one.
    // DamagableResource.IsDamageBlocked returns true when `damageBlockers` is
    // non-empty AND every entry is non-null and activeInHierarchy - so one
    // dummy GameObject toggled on and off is a clean, complete i-frame.
    //
    // WHY IT IS NOT THE SAME AS THE `iframes` SHIP STAT, which is worth being
    // clear about because they look interchangeable:
    //
    //   iFrameDuration   sits on ONE DamagableResource and rate-limits damage
    //                    to THAT resource. A unit has one per resource it can
    //                    lose, so raising it protects health without
    //                    protecting a shield pool.
    //   damageBlockers   blocks EVERYTHING, on every resource, for as long as
    //                    the blocker is active.
    //
    // The blocker is deliberately a child GameObject of the ship rather than a
    // shared one: `activeInHierarchy` is the test, so a blocker parented to a
    // destroyed ship stops blocking by itself.
    public class ModuleForgeInvuln : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge.Invuln");

        // Longest window any installed module asks for. Modules do not stack
        // duration - two 1-second modules give one second, not two - because a
        // stacking immunity window is how you accidentally build permanent
        // invulnerability out of three cheap cards.
        private float _seconds;

        private GameObject _blocker;
        private float _until;
        private bool _wired;

        public void Request(float seconds)
        {
            if (seconds > _seconds)
                _seconds = seconds;

            Wire();
        }

        public void Release()
        {
            _seconds = 0f;
        }

        private void Wire()
        {
            if (_wired)
                return;

            _wired = true;

            var all = GetComponentsInChildren<DamagableResource>(true);

            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || all[i].onDamage == null)
                    continue;

                // The game already invokes this exactly when real damage lands
                // (it is what 97 SfxPlayer components in the game listen to),
                // so there is no need to patch anything to know we were hit.
                all[i].onDamage.AddListener(OnHurt);
            }
        }

        private void OnHurt()
        {
            if (_seconds <= 0f)
                return;

            _until = Time.time + _seconds;

            if (_blocker == null)
                Build();

            if (_blocker != null)
                _blocker.SetActive(true);
        }

        private void Build()
        {
            try
            {
                _blocker = new GameObject("Forge Invulnerability");
                _blocker.transform.SetParent(transform, false);
                _blocker.SetActive(false);

                var all = GetComponentsInChildren<DamagableResource>(true);

                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null)
                        continue;

                    if (all[i].damageBlockers == null)
                        all[i].damageBlockers =
                            new System.Collections.Generic.List<GameObject>();

                    if (!all[i].damageBlockers.Contains(_blocker))
                        all[i].damageBlockers.Add(_blocker);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Building the invulnerability blocker failed: " + e);
                _blocker = null;
            }
        }

        private void Update()
        {
            if (_blocker == null || !_blocker.activeSelf)
                return;

            if (Time.time >= _until)
                _blocker.SetActive(false);
        }

        public static ModuleForgeInvuln For(Unit.Data unit)
        {
            Unit u = ModuleForgeUnits.Find(unit);

            if (u == null)
                return null;

            var inv = u.GetComponent<ModuleForgeInvuln>();

            if (inv == null)
                inv = u.gameObject.AddComponent<ModuleForgeInvuln>();

            return inv;
        }
    }
}
