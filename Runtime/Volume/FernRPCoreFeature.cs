using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace UnityEngine.Rendering.FernRenderPipeline
{
    [Serializable]
    public class FernRPCoreFeature : ScriptableRendererFeature
    {

        [SerializeField]
        internal FernRPData m_FernRPData;
        
        private FernCoreFeatureRenderPass m_BeforeOpaquePass, m_AfterOpaqueAndSkyPass, m_BeforeCoreFeaturePass, m_AfterCoreFeaturePass;

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_BeforeOpaquePass.PrepareRenderers(ref renderingData))
            {
                m_BeforeOpaquePass.Setup(renderer);
                renderer.EnqueuePass(m_BeforeOpaquePass);
            }
            if (m_AfterOpaqueAndSkyPass.PrepareRenderers(ref renderingData))
            {
                m_AfterOpaqueAndSkyPass.Setup(renderer);
                renderer.EnqueuePass(m_AfterOpaqueAndSkyPass);
            }
            if (m_BeforeCoreFeaturePass.PrepareRenderers(ref renderingData))
            {
                m_BeforeCoreFeaturePass.Setup(renderer);
                renderer.EnqueuePass(m_BeforeCoreFeaturePass);
            }
        }

        public override void Create()
        {
            List<FernRPFeatureRenderer> beforeOpaqueRenderers = new List<FernRPFeatureRenderer>();
            List<FernRPFeatureRenderer> afterOpaqueAndSkyRenderers = new List<FernRPFeatureRenderer>();
            List<FernRPFeatureRenderer> beforePostProcessRenderers = new List<FernRPFeatureRenderer>();
            List<FernRPFeatureRenderer> afterPostProcessRenderers = new List<FernRPFeatureRenderer>();

            beforeOpaqueRenderers.Add(new AmbientProbeUpdatePass(m_FernRPData));
            beforeOpaqueRenderers.Add(new DepthOffsetRender());
            beforePostProcessRenderers.Add(new EdgeDetectionEffectRenderer());
            
            m_BeforeOpaquePass = new FernCoreFeatureRenderPass(FernPostProcessInjectionPoint.BeforeOpaque, beforeOpaqueRenderers);
            m_AfterOpaqueAndSkyPass = new FernCoreFeatureRenderPass(FernPostProcessInjectionPoint.AfterOpaqueAndSky, afterOpaqueAndSkyRenderers);
            m_BeforeCoreFeaturePass = new FernCoreFeatureRenderPass(FernPostProcessInjectionPoint.BeforePostProcess, beforePostProcessRenderers);
            m_AfterCoreFeaturePass = new FernCoreFeatureRenderPass(FernPostProcessInjectionPoint.AfterPostProcess, afterPostProcessRenderers);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            m_BeforeOpaquePass.Dispose();
            m_AfterOpaqueAndSkyPass.Dispose();
            m_BeforeCoreFeaturePass.Dispose();
            m_AfterCoreFeaturePass.Dispose();
        }
    }

    /// <summary>
    /// A render pass for executing custom post processing renderers.
    /// </summary>
    public class FernCoreFeatureRenderPass : ScriptableRenderPass
    {
        private List<ProfilingSampler> m_ProfilingSamplers;
        private string m_PassName;
        private FernPostProcessInjectionPoint injectionPoint;
        private List<FernRPFeatureRenderer> m_PostProcessRenderers;
        private List<int> m_ActivePostProcessRenderers;
        private Material uber_Material;
        private RenderTextureDescriptor m_Descriptor;
        private ProfilingSampler m_UberProfilingSampler = new ProfilingSampler("Fern Uber Pass");
        private ProfilingSampler m_CopyProfilingSampler = new ProfilingSampler("Fern Full Screen Copy Pass");
        private static MaterialPropertyBlock s_SharedPropertyBlock = new MaterialPropertyBlock();

        public class PostProcessRTHandles
        {
            public RTHandle m_Source;
            public RTHandle m_Dest;
            public RTHandle m_cameraDepth;
        }

        public PostProcessRTHandles m_rtHandles = new PostProcessRTHandles();

        /// <summary>
        /// Gets whether this render pass has any post process renderers to execute
        /// </summary>
        public bool HasPostProcessRenderers => m_PostProcessRenderers.Count != 0;

        private ScriptableRenderer m_Render = null;

        /// <summary>
        /// Construct the render pass
        /// </summary>
        /// <param name="injectionPoint">The post processing injection point</param>
        /// <param name="classes">The list of classes for the renderers to be executed by this render pass</param>
        public FernCoreFeatureRenderPass(FernPostProcessInjectionPoint injectionPoint, List<FernRPFeatureRenderer> renderers)
        {
            this.injectionPoint = injectionPoint;
            this.m_ProfilingSamplers = new List<ProfilingSampler>(renderers.Count);
            this.m_PostProcessRenderers = renderers;
            foreach (var renderer in renderers)
            {
                // Get renderer name and add it to the names list
                var attribute = FernRenderAttribute.GetAttribute(renderer.GetType());
                m_ProfilingSamplers.Add(new ProfilingSampler(attribute?.Name));
            }

            // Pre-allocate a list for active renderers
            this.m_ActivePostProcessRenderers = new List<int>(renderers.Count);
            // Set render pass event and name based on the injection point.
            switch (injectionPoint)
            {
                case FernPostProcessInjectionPoint.BeforeOpaque:
                    renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
                    m_PassName = "Fern Volume before Opaque";
                    break;
                case FernPostProcessInjectionPoint.AfterOpaqueAndSky:
                    renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
                    m_PassName = "Fern Volume after Opaque & Sky";
                    break;
                case FernPostProcessInjectionPoint.BeforePostProcess:
                    renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing; // TODO： Should After Motion Vector
                    m_PassName = "Fern Volume before PostProcess";
                    break;
                case FernPostProcessInjectionPoint.AfterPostProcess:
                    renderPassEvent = RenderPassEvent.AfterRendering;
                    m_PassName = "Fern Volume after PostProcess";
                    break;
            }
        }

        /// <summary>
        /// Setup Data
        /// </summary>
        public void Setup(ScriptableRenderer renderer)
        {
            m_Render = renderer;
            
        }

        /// <summary>
        /// cameraColorTargetHandle can only be obtained in SRP render
        /// </summary>
        /// <param name="cmd"></param>
        /// <param name="renderingData"></param>
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            base.OnCameraSetup(cmd, ref renderingData);

            m_Descriptor = renderingData.cameraData.cameraTargetDescriptor;
            m_Descriptor.useMipMap = false;
            m_Descriptor.autoGenerateMips = false;

            m_rtHandles.m_Source = m_Render.cameraColorTargetHandle;
            m_rtHandles.m_cameraDepth = m_Render.cameraDepthTargetHandle;
        }

        /// <summary>
        /// Prepares the renderer for executing on this frame and checks if any of them actually requires rendering
        /// </summary>
        /// <param name="renderingData">Current rendering data</param>
        /// <returns>True if any renderer will be executed for the given camera. False Otherwise.</returns>
        public bool PrepareRenderers(ref RenderingData renderingData)
        {
            // See if current camera is a scene view camera to skip renderers with "visibleInSceneView" = false.
            bool isSceneView = renderingData.cameraData.cameraType == CameraType.SceneView;

            // Here, we will collect the inputs needed by all the custom post processing effects
            ScriptableRenderPassInput passInput = ScriptableRenderPassInput.None;
            
            if (uber_Material == null)
            {
                uber_Material = CoreUtils.CreateEngineMaterial("Hidden/FernRender/PostProcess/FernVolumeUber");
            }

            // Collect the active renderers
            m_ActivePostProcessRenderers.Clear();
            for (int index = 0; index < m_PostProcessRenderers.Count; index++)
            {
                var ppRenderer = m_PostProcessRenderers[index];
                // Skips current renderer if "visibleInSceneView" = false and the current camera is a scene view camera. 
                if (isSceneView && !ppRenderer.visibleInSceneView) continue;
                // Setup the camera for the renderer and if it will render anything, add to active renderers and get its required inputs
                if (ppRenderer.Setup(ref renderingData, injectionPoint, uber_Material))
                {
                    m_ActivePostProcessRenderers.Add(index);
                    passInput |= ppRenderer.input;
                }
            }

            // Configure the pass to tell the renderer what inputs we need
            ConfigureInput(passInput);

            // return if no renderers are active
            return m_ActivePostProcessRenderers.Count != 0;
        }

        

        /// <summary>
        /// Execute the custom post processing renderers
        /// </summary>
        /// <param name="context">The scriptable render context</param>
        /// <param name="renderingData">Current rendering data</param>
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            ref var cameraData = ref renderingData.cameraData;

            CommandBuffer cmd = CommandBufferPool.Get(m_PassName);
            
            // Disable obsolete warning for internal usage
            #pragma warning disable CS0618
            PostProcessUtils.SetSourceSize(cmd, cameraData.renderer.cameraColorTargetHandle);
            #pragma warning restore CS0618
            
            for (int index = 0; index < m_ActivePostProcessRenderers.Count; ++index)
            {
                var rendererIndex = m_ActivePostProcessRenderers[index];
                var fernPostProcessRenderer = m_PostProcessRenderers[rendererIndex];
                if (!fernPostProcessRenderer.Initialized)
                    fernPostProcessRenderer.InitializeInternal();
                using (new ProfilingScope(cmd, m_ProfilingSamplers[rendererIndex]))
                {
                    Render(cmd, context, fernPostProcessRenderer, ref renderingData);
                }
            }

            if (injectionPoint == FernPostProcessInjectionPoint.BeforePostProcess)
            {
                
                m_rtHandles.m_Dest = m_Render.GetCameraColorFrontBuffer(cmd);
                m_Render.SwapColorBuffer(cmd);
                Blitter.BlitCameraTexture(cmd, m_rtHandles.m_Source, m_rtHandles.m_Dest, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, uber_Material, 0);
            }
            
            // Send command buffer for execution, then release it.
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        void Render(CommandBuffer cmd, ScriptableRenderContext context, FernRPFeatureRenderer fernPostRenderer, ref RenderingData renderingData)
        {
            fernPostRenderer.Render(cmd, context, m_rtHandles, ref renderingData, injectionPoint);
        }
        
        private class FernUberPostPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle source;
            internal Material material;
            internal UniversalCameraData cameraData;
        }
        
        private class CopyPassData
        {
            internal TextureHandle inputTexture;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            for (int index = 0; index < m_ActivePostProcessRenderers.Count; ++index)
            {
                var rendererIndex = m_ActivePostProcessRenderers[index];
                var fernPostProcessRenderer = m_PostProcessRenderers[rendererIndex];
                if (!fernPostProcessRenderer.Initialized)
                    fernPostProcessRenderer.InitializeInternal();
                fernPostProcessRenderer.RecordRenderGraph(renderGraph, frameData);
            }

            if (injectionPoint == FernPostProcessInjectionPoint.BeforePostProcess)
            {
                UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                   
                var targetDesc = renderGraph.GetTextureDesc(resourcesData.cameraColor);
                targetDesc.name = "_CameraColorFullScreenPass";
                targetDesc.clearBuffer = false;

                var source = resourcesData.activeColorTexture;
                var destination = renderGraph.CreateTexture(targetDesc);
                
                using (var builder = renderGraph.AddRasterRenderPass<CopyPassData>("Copy Color Full Screen", out var passData, m_CopyProfilingSampler))
                {
#if ENABLE_VR && ENABLE_XR_MODULE
                    // TODO
#endif
                     passData.inputTexture = source;
                    builder.UseTexture(passData.inputTexture, AccessFlags.Read);
                    
                    builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                    
                    builder.SetRenderFunc(static (CopyPassData data, RasterGraphContext rgContext) =>
                    {
                        ExecuteCopyColorPass(rgContext.cmd, data.inputTexture);
                    });
                    //Swap for next pass;
                    source = destination;       
                }
                
                destination = resourcesData.activeColorTexture;
                
                using (var builder =
                       renderGraph.AddRasterRenderPass<FernUberPostPassData>("Fern Uber Post Processing", out var passData, m_UberProfilingSampler))
                {
                    passData.source = source;
                    
                    if(passData.source.IsValid())
                        builder.UseTexture(passData.source, AccessFlags.Read);
                    
                    builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                
                    passData.cameraData = cameraData;
                    passData.material = uber_Material;
                
                    builder.SetRenderFunc((FernUberPostPassData data, RasterGraphContext rgContext) =>
                    {
                        ExecuteMainPass(rgContext.cmd, data.source, data.material, 0);
                    });
                }
            }
        }
        
        private static void ExecuteCopyColorPass(RasterCommandBuffer cmd, RTHandle sourceTexture)
        {
            Blitter.BlitTexture(cmd, sourceTexture, new Vector4(1, 1, 0, 0), 0.0f, false);
        }
        
        private static void ExecuteMainPass(RasterCommandBuffer cmd, RTHandle sourceTexture, Material material, int passIndex)
        {
            s_SharedPropertyBlock.Clear();
            if (sourceTexture != null)
                s_SharedPropertyBlock.SetTexture(ShaderPropertyId.blitTexture, sourceTexture);

            // We need to set the "_BlitScaleBias" uniform for user materials with shaders relying on core Blit.hlsl to work
            s_SharedPropertyBlock.SetVector(ShaderPropertyId.blitScaleBias, new Vector4(1, 1, 0, 0));

            cmd.DrawProcedural(Matrix4x4.identity, material, passIndex, MeshTopology.Triangles, 3, 1, s_SharedPropertyBlock);
        }

        public void Dispose()
        {
            for (int index = 0; index < m_ActivePostProcessRenderers.Count; ++index)
            {
                var rendererIndex = m_ActivePostProcessRenderers[index];
                var fernPostProcessRenderer = m_PostProcessRenderers[rendererIndex];
                fernPostProcessRenderer.Dispose();
            }
            m_rtHandles.m_Source?.Release();
            m_rtHandles.m_Dest?.Release();
            m_rtHandles.m_cameraDepth?.Release();
        }

        public RenderTextureDescriptor GetCompatibleDescriptor()
            => GetCompatibleDescriptor(m_Descriptor.width, m_Descriptor.height, m_Descriptor.graphicsFormat);

        public RenderTextureDescriptor GetCompatibleDescriptor(int width, int height, GraphicsFormat format,
            DepthBits depthBufferBits = DepthBits.None)
            => GetCompatibleDescriptor(m_Descriptor, width, height, format, depthBufferBits);

        internal static RenderTextureDescriptor GetCompatibleDescriptor(RenderTextureDescriptor desc, int width,
            int height, GraphicsFormat format, DepthBits depthBufferBits = DepthBits.None)
        {
            desc.depthBufferBits = (int)depthBufferBits;
            desc.msaaSamples = 1;
            desc.width = width;
            desc.height = height;
            desc.graphicsFormat = format;
            return desc;
        }
        
        static private void ScaleViewportAndBlit(RasterCommandBuffer cmd, RTHandle sourceTextureHdl, RTHandle dest, UniversalCameraData cameraData, Material material, bool hasFinalPass)
        {
            Vector4 scaleBias = RenderingUtils.GetFinalBlitScaleBias(sourceTextureHdl, dest, cameraData);
            RenderTargetIdentifier cameraTarget = BuiltinRenderTextureType.CameraTarget;
#if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData.xr.enabled)
                cameraTarget = cameraData.xr.renderTarget;
#endif
            if (dest.nameID == cameraTarget || cameraData.targetTexture != null)
            {
                if (hasFinalPass || !cameraData.resolveFinalTarget)
                {
                    // Intermediate target can be scaled with render scale.
                    // camera.pixelRect is the viewport of the final target in pixels.
                    // Calculate scaled viewport for the intermediate target,
                    // for example when inside a camera stack (non-final pass).
                    var camViewportNormalized = cameraData.camera.rect;
                    var targetWidth = cameraData.cameraTargetDescriptor.width;
                    var targetHeight = cameraData.cameraTargetDescriptor.height;
                    var scaledTargetViewportInPixels = new Rect(
                        camViewportNormalized.x * targetWidth,
                        camViewportNormalized.y * targetHeight,
                        camViewportNormalized.width * targetWidth,
                        camViewportNormalized.height * targetHeight);
                    cmd.SetViewport(scaledTargetViewportInPixels);
                }
                else
                    cmd.SetViewport(cameraData.pixelRect);
            }
            
            Blitter.BlitTexture(cmd, sourceTextureHdl, scaleBias, material, 0);
        }
    }
}