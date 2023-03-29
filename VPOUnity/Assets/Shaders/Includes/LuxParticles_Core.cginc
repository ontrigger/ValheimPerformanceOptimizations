#ifndef LUXPARTICLES_CORE_INCLUDED
#define LUXPARTICLES_CORE_INCLUDED

		float _Lux_AmbientMode; // 0: color, 1 = gradient, 2 = sh lighting
		UNITY_INSTANCING_BUFFER_START(Props)
			UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
		UNITY_INSTANCING_BUFFER_END(Props)
		sampler2D _MainTex;
		float4 _MainTex_ST;
		
		float4 _TintColor;

		#if defined(EFFECT_HUE_VARIATION)
			sampler2D _AlbedoMap;
		#endif

		#if defined(_EMISSION)
			sampler2D _EmissionMap;
			float _EmissionScale;
		#endif
		
		float _ShadowExtrude;
		float4 _CamFadeDistance;

/* moved to Utils include
		fixed3 _AmbientColor;
		fixed _LightWrap;
		fixed _Translucency;
		fixed _TranslucencyWrap;
*/

		#ifdef SOFTPARTICLES_ON
			UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
			float _InvFade;
		#endif 

		struct appdata
		{
			float4 vertex : POSITION;
			#ifdef _FLIPBOOK_BLENDING
				float4 uv : TEXCOORD0;
				float blend : TEXCOORD1;
			#else
				float2 uv : TEXCOORD0;
			#endif
			float4 color : COLOR;
			float3 normal : NORMAL;
			UNITY_VERTEX_INPUT_INSTANCE_ID
		};

		struct v2f
		{
			float4 vertex : SV_POSITION;

			#ifdef _FLIPBOOK_BLENDING
				float4 uv : TEXCOORD0;
			#else
				float2 uv : TEXCOORD0;
			#endif
			
			fixed4 color : COLOR;

			float4 LightDirAndAtten0 : TEXCOORD1;
			#if defined(GEOM_TYPE_BRANCH)
				float4 LightDirAndAtten1 : TEXCOORD2;
				float4 LightDirAndAtten2 : TEXCOORD3;
				float4 LightDirAndAtten3 : TEXCOORD4;
			#endif

			half4 vertexLighting : TEXCOORD5; //xyz: lighting, w: atten (if per vertex)
			#if defined(GEOM_TYPE_MESH)
				float3 worldPos : TEXCOORD6;
			#endif

			#ifdef _FLIPBOOK_BLENDING
				float4 worldViewDir : TEXCOORD7; // w contains blend
			#else
				float3 worldViewDir : TEXCOORD7;
			#endif

			#if defined(SOFTPARTICLES_ON) || defined(WBOIT)
            	float4 projPos : TEXCOORD8;
            #endif
			
			UNITY_FOG_COORDS(9)

			UNITY_VERTEX_OUTPUT_STEREO
		};

		v2f vert (appdata v)
		{
			v2f o;
		    UNITY_SETUP_INSTANCE_ID(v);
			UNITY_INITIALIZE_OUTPUT(v2f,o);

		//	Calculate worldPos upfront
			float4 worldPos = mul(unity_ObjectToWorld, v.vertex);
			#if defined(GEOM_TYPE_MESH)
				o.worldPos = worldPos.xyz;
			#endif
			
			float3 quadPivotPosVS = UnityObjectToViewPos(float3(0,0,0)); // mul(UNITY_MATRIX_MV, float4(quadPivotPosWS, 1)).xyz;

			//get transform.lossyScale using:
			//https://forum.unity.com/threads/can-i-get-the-scale-in-the-transform-of-the-object-i-attach-a-shader-to-if-so-how.418345/
			float2 scaleXY_WS = float2(
				length(float3(unity_ObjectToWorld[0].x, unity_ObjectToWorld[1].x, unity_ObjectToWorld[2].x)), // scale x axis
				length(float3(unity_ObjectToWorld[0].y, unity_ObjectToWorld[1].y, unity_ObjectToWorld[2].y)) // scale y axis
			);

			float3 posVS = quadPivotPosVS + float3(v.vertex.xy * scaleXY_WS,0);

			o.vertex = mul(UNITY_MATRIX_P,float4(posVS,1));

			o.uv.xy = TRANSFORM_TEX(v.uv.xy, _MainTex);
			#ifdef _FLIPBOOK_BLENDING
				o.uv.zw = TRANSFORM_TEX(v.uv.zw, _MainTex);
				o.worldViewDir.w = v.blend;
			#endif

			o.color = v.color * UNITY_ACCESS_INSTANCED_PROP(Props, _Color) * _TintColor;

			#if defined(SOFTPARTICLES_ON) || defined(WBOIT)
				#if defined(SOFTPARTICLES_ON)
            		o.projPos = ComputeScreenPos (o.vertex);
            	#endif
            	COMPUTE_EYEDEPTH(o.projPos.z);
            #endif
			
		//	Particle Lighting // NOTE: v.normal = worldNormal if normal Direction is set to 1

		//	Calculate ViewPos (optimized)
			float3 viewPos = mul(UNITY_MATRIX_V, float4(worldPos.xyz, 1.0)).xyz;
		//	Calculate normal in ViewSpace
			float3 viewNormal = normalize ( mul( (float3x3)UNITY_MATRIX_IT_MV, v.normal ) );

			o.LightDirAndAtten0 = LightDirAndAtten(viewPos, 0);
			
			#if defined(GEOM_TYPE_BRANCH)
				o.LightDirAndAtten1 = LightDirAndAtten(viewPos, 1);
				o.LightDirAndAtten2 = LightDirAndAtten(viewPos, 2);
				o.LightDirAndAtten3 = LightDirAndAtten(viewPos, 3);
			#else
				o.vertexLighting.xyz = ApplyVertexLight(viewNormal, viewPos, 1);
				o.vertexLighting.xyz += ApplyVertexLight(viewNormal, viewPos, 2);
				o.vertexLighting.xyz += ApplyVertexLight(viewNormal, viewPos, 3);
			#endif
		
		//	Ambient lighting
			#if !defined(GEOM_TYPE_BRANCH_DETAIL)
			//	Color
				if( _Lux_AmbientMode == 0) {
					o.vertexLighting.xyz += UNITY_LIGHTMODEL_AMBIENT.xyz;
				}
			//	Trilight
				else if ( _Lux_AmbientMode == 1) {
					half3 ambientG = unity_AmbientSky * saturate(v.normal.y) + unity_AmbientGround * saturate(-v.normal.y) + unity_AmbientEquator * (1 - abs(v.normal.y));
					o.vertexLighting.xyz += ambientG;
				}
			//	SH Lighting
				else {
					#ifdef UNITY_COLORSPACE_GAMMA
						half3 ambientL = Lux_ShadeSH(float4(v.normal, 1) );
						o.vertexLighting.xyz += LinearToGammaSpace(ambientL);
					#else
						o.vertexLighting.xyz += Lux_ShadeSH(float4(v.normal, 1) );
					#endif
				}
			#endif

		//	WorldViewDir 
			o.worldViewDir.xyz = normalize(_WorldSpaceCameraPos - worldPos);

		//	Get shadow
			#if defined(GEOM_TYPE_FROND)
				#if !defined(GEOM_TYPE_MESH)
					o.vertexLighting.w = GetLightAttenuation(worldPos.xyz);
				#endif
			#endif

		//	Alpha based on distance to camera
		//	Near
//			o.color.a *= saturate( ( -viewPos.z - _ProjectionParams.y) / _CamFadeDistance.x );


o.color.a *= saturate( ( -viewPos.z - _CamFadeDistance.w - _ProjectionParams.y) / _CamFadeDistance.x );

		//	Far
			o.color.a *= saturate( (_CamFadeDistance.y + viewPos.z) / _CamFadeDistance.z );

			UNITY_TRANSFER_FOG(o,o.vertex);
			return o;
		}




#endif // LUXPARTICLES_CORE_INCLUDED