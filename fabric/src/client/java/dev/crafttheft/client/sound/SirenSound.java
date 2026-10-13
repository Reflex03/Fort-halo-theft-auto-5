package dev.crafttheft.client.sound;

import net.minecraft.client.resources.sounds.AbstractTickableSoundInstance;
import net.minecraft.client.resources.sounds.SoundInstance;
import net.minecraft.sounds.SoundSource;
import net.minecraft.util.Mth;

import dev.crafttheft.content.ModSounds;
import dev.crafttheft.vehicle.CarEntity;

/**
 * The police siren loop (sources row gta_snd_siren, the police vehicle row's hornSound) following a chasing police
 * car. With the vanilla placeholder (a note block blip) the pitch is swept to make it wail; the real GTA siren
 * plays as recorded.
 */
public class SirenSound extends AbstractTickableSoundInstance {
	private final CarEntity car;
	private int age;
	public static int started;

	public SirenSound(CarEntity car) {
		super(ModSounds.of(car.spec.hornSound()), SoundSource.HOSTILE, SoundInstance.createUnseededRandom());
		this.car = car;
		this.looping = true;
		this.delay = 0;
		this.volume = 1.5f;
		this.x = car.getX(); this.y = car.getY(); this.z = car.getZ();
		started++;
	}

	@Override
	public void tick() {
		if (car.isRemoved() || !car.sirenOn()) { stop(); return; }
		x = car.getX(); y = car.getY() + 1; z = car.getZ();
		age++;
		boolean placeholder = sound != null && sound.getLocation().getNamespace().equals("minecraft");
		pitch = placeholder ? 1.0f + 0.35f * Mth.sin(age * 0.18f) : 1.0f;
	}
}
