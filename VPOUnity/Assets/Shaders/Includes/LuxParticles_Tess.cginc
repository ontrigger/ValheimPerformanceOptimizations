

#ifndef LUXPARTICLES_TESS_INCLUDED
#define LUXPARTICLES_TESS_INCLUDED

	#ifdef UNITY_CAN_COMPILE_TESSELLATION

	#include "Tessellation.cginc"

	float _Tess;
	float2 _TessRange;

		struct TessVertex {
	        float4 vertex : INTERNALTESSPOS;
	        float3 normal : NORMAL;
	        float4 color : COLOR;
	        #ifdef _FLIPBOOK_BLENDING
				float4 texcoord : TEXCOORD0;
				float blend : TEXCOORD1;
			#else
				float2 texcoord : TEXCOORD0;
			#endif
	        //float4 texcoord : TEXCOORD0;
	        //UNITY_VERTEX_INPUT_INSTANCE_ID
	    };
	    struct OutputPatchConstant {
	        float edge[3]         : SV_TessFactor;
	        float inside          : SV_InsideTessFactor;
	    };
	    TessVertex tessvert (appdata v) {
	        TessVertex o;
	        o.vertex    = v.vertex;
	        o.normal    = v.normal;
	        o.color     = v.color;
	        o.texcoord  = v.uv;
	        #ifdef _FLIPBOOK_BLENDING
				o.blend = v.blend;
			#endif
	        //UNITY_VERTEX_INPUT_INSTANCE_ID
	        return o;
	    }

	    float4 Tessellation(TessVertex v, TessVertex v1, TessVertex v2) {
			return UnityDistanceBasedTess(v.vertex, v1.vertex, v2.vertex, _TessRange.x, _TessRange.y, _Tess);
	    }

	    OutputPatchConstant hullconst (InputPatch<TessVertex,3> v) {
	        OutputPatchConstant o;
	        float4 ts = Tessellation( v[0], v[1], v[2] );
	        o.edge[0] = ts.x;
	        o.edge[1] = ts.y;
	        o.edge[2] = ts.z;
	        o.inside = ts.w;
	        return o;
	    }

	    [domain("tri")]
	    [partitioning("fractional_odd")]
	    [outputtopology("triangle_cw")]
	    [patchconstantfunc("hullconst")]
	    [outputcontrolpoints(3)]
	    TessVertex hs_surf (InputPatch<TessVertex,3> v, uint id : SV_OutputControlPointID) {
	        return v[id];
	    }

	    [domain("tri")]
	    v2f ds_surf (OutputPatchConstant tessFactors, const OutputPatch<TessVertex,3> vi, float3 bary : SV_DomainLocation) {
	        appdata v = (appdata)0;
	        v.vertex = vi[0].vertex*bary.x + vi[1].vertex*bary.y + vi[2].vertex*bary.z;
	        v.normal = vi[0].normal*bary.x + vi[1].normal*bary.y + vi[2].normal*bary.z;
	        v.color = vi[0].color*bary.x + vi[1].color*bary.y + vi[2].color*bary.z;
	        v.uv = vi[0].texcoord*bary.x + vi[1].texcoord*bary.y + vi[2].texcoord*bary.z;
	        #ifdef _FLIPBOOK_BLENDING
				v.blend = vi[0].blend;
			#endif
	    //  New call the regular vertex function
	        v2f o = vert(v);
	        return o;
	    }
	#endif



#endif // LUXPARTICLES_TESS_INCLUDED