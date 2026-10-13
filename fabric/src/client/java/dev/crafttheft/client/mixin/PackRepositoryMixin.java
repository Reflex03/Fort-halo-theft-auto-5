package dev.crafttheft.client.mixin;

import java.util.LinkedHashSet;
import java.util.Set;

import org.spongepowered.asm.mixin.Final;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Mutable;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

import net.minecraft.client.resources.ClientPackSource;
import net.minecraft.server.packs.repository.PackRepository;
import net.minecraft.server.packs.repository.RepositorySource;

import dev.crafttheft.client.cache.CachePacks;

/** hooks row cache_pack: adds the cache packs to the CLIENT pack repository only (as Fabric API's own mixin does). */
@Mixin(PackRepository.class)
public abstract class PackRepositoryMixin {
	@Shadow @Final @Mutable private Set<RepositorySource> sources;

	@Inject(method = "<init>", at = @At("RETURN"))
	private void crafttheft$addCachePacks(RepositorySource[] given, CallbackInfo ci) {
		boolean client = false;
		for (RepositorySource s : given) if (s instanceof ClientPackSource) client = true;
		if (!client) return;
		Set<RepositorySource> copy = new LinkedHashSet<>(this.sources);
		CachePacks.addSources(copy);
		this.sources = copy;
	}
}
