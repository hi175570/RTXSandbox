using System.Numerics;
using RTXSandbox.ModAPI;

namespace RTXSandbox.Gameplay;

public sealed class PlayerSurvival
{
    public const float MaximumHealth = 20f;
    public const float MaximumOxygen = 10f;
    public const float MaximumHunger = 20f;

    private readonly PlayerController _player;
    private readonly Func<PlayerDamageEvent, PlayerDamageEvent> _publishDamage;
    private readonly Func<PlayerRespawnEvent, PlayerRespawnEvent> _publishRespawn;
    private float _drowningTick;
    private float _starvationTick;
    private float _regenerationTick;

    public bool Enabled { get; set; } = true;
    public float DamageMultiplier { get; set; } = 1f;
    public float Health { get; private set; } = MaximumHealth;
    public float Oxygen { get; private set; } = MaximumOxygen;
    public float Hunger { get; private set; } = MaximumHunger;
    public Vector3 SpawnPosition { get; private set; }
    public int DeathCount { get; private set; }
    public string LastDamageCause { get; private set; } = string.Empty;

    public PlayerSurvival(
        PlayerController player,
        Vector3 spawnPosition,
        Func<PlayerDamageEvent, PlayerDamageEvent> publishDamage,
        Func<PlayerRespawnEvent, PlayerRespawnEvent> publishRespawn)
    {
        _player = player;
        SpawnPosition = spawnPosition;
        _publishDamage = publishDamage;
        _publishRespawn = publishRespawn;
    }

    public void Update(float deltaSeconds)
    {
        deltaSeconds = Math.Clamp(deltaSeconds, 0f, 0.05f);
        if (!Enabled)
        {
            Oxygen = MaximumOxygen;
            Hunger = MaximumHunger;
            return;
        }

        Hunger = Math.Max(0f, Hunger - deltaSeconds * 0.016f);
        if (Hunger <= 0f)
        {
            _starvationTick += deltaSeconds;
            if (_starvationTick >= 2f)
            {
                _starvationTick -= 2f;
                Damage(1f, PlayerDamageCause.Starvation);
            }
        }
        else
        {
            _starvationTick = 0f;
        }

        if (Hunger >= 18f && Health < MaximumHealth)
        {
            _regenerationTick += deltaSeconds;
            if (_regenerationTick >= 4f)
            {
                _regenerationTick -= 4f;
                Heal(1f);
                Hunger = Math.Max(0f, Hunger - 1f);
            }
        }
        else
        {
            _regenerationTick = 0f;
        }

        if (_player.IsHeadUnderwater)
        {
            Oxygen = Math.Max(0f, Oxygen - deltaSeconds);
            if (Oxygen <= 0f)
            {
                _drowningTick += deltaSeconds;
                if (_drowningTick >= 1f)
                {
                    _drowningTick -= 1f;
                    Damage(2f, PlayerDamageCause.Drowning);
                }
            }
        }
        else
        {
            Oxygen = Math.Min(MaximumOxygen, Oxygen + deltaSeconds * 3f);
            _drowningTick = 0f;
        }

        if (_player.LandedThisFrame && _player.LastLandingSpeed > 10f)
        {
            Damage((_player.LastLandingSpeed - 10f) * 1.35f, PlayerDamageCause.Fall);
        }

        Vector3 position = _player.Position;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) || position.Y < -8f)
        {
            Damage(MaximumHealth, PlayerDamageCause.Void);
        }
    }

    public bool Damage(float amount, PlayerDamageCause cause = PlayerDamageCause.Mod)
    {
        if (!Enabled || amount <= 0f)
        {
            return false;
        }

        PlayerDamageEvent damage = _publishDamage(new PlayerDamageEvent(amount * DamageMultiplier, cause));
        if (damage.IsCancelled || damage.Amount <= 0f)
        {
            return false;
        }

        Health = Math.Max(0f, Health - damage.Amount);
        LastDamageCause = cause.ToString();
        if (Health <= 0f)
        {
            Respawn();
        }
        return true;
    }

    public void Heal(float amount) => Health = Math.Min(MaximumHealth, Health + Math.Max(0f, amount));

    public void SetSpawn(Vector3 position) => SpawnPosition = position;

    public bool Eat(float foodValue, float healValue = 0f)
    {
        if (foodValue <= 0f || Hunger >= MaximumHunger - 0.01f)
        {
            return false;
        }
        Hunger = Math.Min(MaximumHunger, Hunger + foodValue);
        Heal(healValue);
        return true;
    }

    public void AddExhaustion(float hungerCost) =>
        Hunger = Math.Max(0f, Hunger - Math.Max(0f, hungerCost));

    public void Restore(float health, float oxygen, float hunger = MaximumHunger)
    {
        Health = Math.Clamp(health, 0.1f, MaximumHealth);
        Oxygen = Math.Clamp(oxygen, 0f, MaximumOxygen);
        Hunger = Math.Clamp(hunger, 0f, MaximumHunger);
    }

    public void Respawn()
    {
        DeathCount++;
        PlayerRespawnEvent respawn = _publishRespawn(new PlayerRespawnEvent(SpawnPosition, DeathCount));
        _player.Teleport(respawn.Position);
        Health = MaximumHealth;
        Oxygen = MaximumOxygen;
        Hunger = Math.Max(Hunger, MaximumHunger * 0.6f);
        _drowningTick = 0f;
        _starvationTick = 0f;
        _regenerationTick = 0f;
    }
}
