package dev.crafttheft.heal;

import java.util.List;

import net.minecraft.ChatFormatting;
import net.minecraft.network.chat.Component;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.sounds.SoundEvent;
import net.minecraft.sounds.SoundEvents;
import net.minecraft.sounds.SoundSource;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.InteractionResultHolder;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.TooltipFlag;
import net.minecraft.world.item.UseAnim;
import net.minecraft.world.level.Level;

import dev.crafttheft.content.ModSounds;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.shield.ShieldSystem;
import dev.crafttheft.shield.ShieldTuning;

/**
 * A Fortnite heal (one per consumables row): use for useTicks, then restore health up to healthCap and/or shield
 * up to shieldCap (Fortnite 0-100 scale; systems.combat.fortniteHpToMc and systems.shield.pointsPerMcDamage map
 * them to Minecraft). Unusable when it would do nothing, like in Fortnite.
 */
public class FortniteHealItem extends Item {
	public final Sheets.Consumable row;
	public static int heals;

	public FortniteHealItem(Sheets.Consumable row, Properties props) {
		super(props);
		this.row = row;
	}

	public boolean isDrink() { return row.useSound().equals("fn_snd_drink"); }

	@Override public int getUseDuration(ItemStack stack, LivingEntity entity) { return row.useTicks(); }

	@Override public UseAnim getUseAnimation(ItemStack stack) { return isDrink() ? UseAnim.DRINK : UseAnim.BOW; }

	/** The Fortnite use sound plays once at the start (below); vanilla's repeating sip sound is muted. */
	@Override public SoundEvent getDrinkingSound() { return SoundEvents.EMPTY; }

	public float healthCapMc() { return row.healthCap() * Sheets.COMBAT_FORTNITE_HP_TO_MC; }

	public boolean wouldHelp(Player p) {
		boolean health = row.health() > 0 && p.getHealth() < Math.min(p.getMaxHealth(), healthCapMc()) - 0.01f;
		boolean shield = row.shield() > 0 && p.getAbsorptionAmount() < Math.min(p.getMaxAbsorption(), ShieldTuning.toMc(row.shieldCap())) - 0.01f;
		return health || shield;
	}

	@Override
	public InteractionResultHolder<ItemStack> use(Level level, Player player, InteractionHand hand) {
		ItemStack st = player.getItemInHand(hand);
		if (!wouldHelp(player)) return InteractionResultHolder.fail(st);
		player.startUsingItem(hand);
		if (!level.isClientSide) {
			level.playSound(null, player.getX(), player.getY(), player.getZ(), ModSounds.of(row.useSound()), SoundSource.PLAYERS, 1f, 1f);
			dev.crafttheft.CraftTheft.LOG.info("[heal] {} started using {} ({} ticks)", player.getName().getString(), row.id(), row.useTicks());
		}
		return InteractionResultHolder.consume(st);
	}

	@Override
	public ItemStack finishUsingItem(ItemStack stack, Level level, LivingEntity entity) {
		if (entity instanceof ServerPlayer sp) {
			float h0 = sp.getHealth(), s0 = sp.getAbsorptionAmount();
			if (row.health() > 0) {
				float cap = Math.min(sp.getMaxHealth(), healthCapMc());
				float cur = sp.getHealth();
				if (cur < cap) sp.setHealth(Math.min(cap, cur + row.health() * Sheets.COMBAT_FORTNITE_HP_TO_MC));
			}
			if (row.shield() > 0) ShieldSystem.add(sp, row.shield(), row.shieldCap());
			heals++;
			stack.consume(1, sp);
			dev.crafttheft.CraftTheft.LOG.info("[heal] {} used {}: health {} -> {}, shield {} -> {}", sp.getName().getString(), row.id(), h0, sp.getHealth(), s0, sp.getAbsorptionAmount());
		}
		return stack;
	}

	@Override
	public void appendHoverText(ItemStack stack, TooltipContext ctx, List<Component> lines, TooltipFlag flag) {
		if (row.health() > 0) lines.add(Component.literal("+" + row.health() + " health (max " + row.healthCap() + ")").withStyle(ChatFormatting.GREEN));
		if (row.shield() > 0) lines.add(Component.literal("+" + row.shield() + " shield (max " + row.shieldCap() + ")").withStyle(ChatFormatting.AQUA));
		lines.add(Component.literal(String.format("%.1f s", row.useTicks() / 20f)).withStyle(ChatFormatting.GRAY));
	}
}
