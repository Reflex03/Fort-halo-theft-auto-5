package dev.crafttheft.client.render;

import com.mojang.blaze3d.vertex.PoseStack;
import com.mojang.blaze3d.vertex.VertexConsumer;
import com.mojang.math.Axis;

import net.minecraft.client.renderer.MultiBufferSource;
import net.minecraft.client.renderer.RenderType;

/** Immediate-mode drawing of a RuntimeMesh through vanilla entity render types. */
public final class MeshDraw {
	public static long draws;

	private MeshDraw() {}

	/**
	 * Entity render types are VertexFormat.Mode.QUADS, so each triangle is emitted as a degenerate quad (v0 v1 v2 v2).
	 * That keeps vanilla shaders, lightmap, overlay (hurt flash) and fog. Submeshes on wheel_* nodes spin about X
	 * around the node pivot; front wheels also steer.
	 */
	public static void draw(RuntimeMesh mesh, PoseStack ps, MultiBufferSource buffers, int light, int overlay, float wheelSpin, float steerDeg) {
		draws++;
		for (RuntimeMesh.Submesh sm : mesh.submeshes) {
			VertexConsumer vc = buffers.getBuffer(RenderType.entityCutoutNoCull(sm.texture != null ? sm.texture : MeshCache.MISSING_TEXTURE));
			boolean pushed = false;
			if (sm.node >= 0) {
				RuntimeMesh.Node n = mesh.nodes.get(sm.node);
				if (n.name().startsWith("wheel")) {
					ps.pushPose();
					pushed = true;
					ps.translate(n.px(), n.py(), n.pz());
					if (n.name().endsWith("f") || n.name().contains("_lf") || n.name().contains("_rf")) ps.mulPose(Axis.YP.rotationDegrees(steerDeg));
					ps.mulPose(Axis.XP.rotation(wheelSpin));
					ps.translate(-n.px(), -n.py(), -n.pz());
				}
			}
			PoseStack.Pose pose = ps.last();
			float[] v = sm.verts;
			int[] idx = sm.indices;
			for (int i = 0; i < idx.length; i += 3) {
				vertex(vc, pose, v, idx[i], light, overlay);
				vertex(vc, pose, v, idx[i + 1], light, overlay);
				vertex(vc, pose, v, idx[i + 2], light, overlay);
				vertex(vc, pose, v, idx[i + 2], light, overlay);
			}
			if (pushed) ps.popPose();
		}
	}

	private static void vertex(VertexConsumer vc, PoseStack.Pose pose, float[] v, int i, int light, int overlay) {
		int o = i * 8;
		vc.addVertex(pose, v[o], v[o + 1], v[o + 2])
				.setColor(255, 255, 255, 255)
				.setUv(v[o + 6], v[o + 7])
				.setOverlay(overlay)
				.setLight(light)
				.setNormal(pose, v[o + 3], v[o + 4], v[o + 5]);
	}
}
