[numthreads(1,1,1)]
void ClearCausticPhotons(uint3 id : SV_DispatchThreadID)
{
    _CausticPhotonMetadata[0] = 0;
    _CausticPhotonMetadata[1] = 0;
    _CausticPhotonMetadata[2] = 0;
    _CausticPhotonMetadata[3] = 0;
    _CausticPhotonMetadata[4] = 0;
    _CausticPhotonMetadata[5] = 0;
}

[numthreads(64,1,1)]
void ClearCausticGrid(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)max(0, _CausticGridCellCount))
    {
        _CausticGridCellHeads[id.x] = -1;
    }
}

#if defined(CAUSTIC_PHOTON_TRACE)
int SelectCausticTargetPair(float sample)
{
    int low = 0;
    int high = _NumCausticTargetPairs - 1;
    [loop]
    while (low < high)
    {
        int middle = low + ((high - low) >> 1);
        if (sample <= _CausticTargetPairs[middle].cumulativeProbability)
        {
            high = middle;
        }
        else
        {
            low = middle + 1;
        }
    }
    return low;
}

CausticTargetTriangle SelectCausticTargetTriangle(CausticTargetPair pair, float sample)
{
    int low = pair.triangleStart;
    int high = pair.triangleStart + pair.triangleCount - 1;
    [loop]
    while (low < high)
    {
        int middle = low + ((high - low) >> 1);
        if (sample <= _CausticTargetTriangles[middle].cumulativeProbability)
        {
            high = middle;
        }
        else
        {
            low = middle + 1;
        }
    }
    return _CausticTargetTriangles[low];
}

bool TraceCausticPhotonTransport(
    Ray photonRay,
    RayHit firstHit,
    float3 initialPower,
    inout uint rngState,
    out CausticPhoton photon)
{
    photon.position = float3(0.0f, 0.0f, 0.0f);
    photon.incomingDirection = float3(0.0f, 0.0f, 0.0f);
    photon.power = float3(0.0f, 0.0f, 0.0f);

    float3 throughput = initialPower;
    MediumStack mediumStack = CreateMediumStack(photonRay.origin);
    RayHit hit = firstHit;
    bool hasSpecularEvent = false;
    int maxBounces = clamp(_CausticMaxBounces, 1, 16);

    [loop]
    for (int bounce = 0; bounce < maxBounces; bounce++)
    {
        throughput *= GetActiveMediumSegmentTransmittance(photonRay, hit.distance, mediumStack);
        if (!HasPathEnergy(throughput) || DidHitSky(hit) || DidHitLight(hit))
        {
            return false;
        }
        ApplyFiniteMediumExitAfterSegment(mediumStack, photonRay, hit);

        if (IsCausticReceiver(hit))
        {
            if (!hasSpecularEvent || dot(hit.normal, -photonRay.direction) <= 0.0f)
            {
                return false;
            }

            photon.position = hit.position;
            photon.incomingDirection = photonRay.direction;
            photon.power = throughput;
            return true;
        }

        if (!IsGlassMaterial(hit))
        {
            return false;
        }

        bool entering;
        float2 transitionIndices = GetBoundaryTransitionIndices(mediumStack, hit, entering);
        float3 opticalNormal = SampleDielectricBoundaryNormal(
            GetCausticOpticalNormal(hit),
            hit.geometricNormal,
            photonRay.direction,
            hit.smoothness,
            transitionIndices.x,
            transitionIndices.y,
            rngState);
        float fresnelReflectance = GetFresnelReflectanceForNormal(
            photonRay,
            hit,
            transitionIndices.x,
            transitionIndices.y,
            opticalNormal);
        float reflectionProbability = GetGlassReflectionProbability(fresnelReflectance, hit);
        float transmissionProbability = saturate(hit.transmission) * (1.0f - reflectionProbability);
        float3 transmittedDirection;
        bool canTransmit = RefractSnell(
            photonRay.direction,
            transitionIndices.x,
            transitionIndices.y,
            opticalNormal,
            transmittedDirection);

        if (canTransmit && rand(rngState) < transmissionProbability)
        {
            photonRay.direction = transmittedDirection;
            ApplyMediumTransition(
                mediumStack,
                hit,
                entering ? MediumTransitionEnter : MediumTransitionExit);
        }
        else
        {
            photonRay.direction = normalize(reflect(photonRay.direction, opticalNormal));
        }

        hasSpecularEvent = true;
        photonRay.origin = hit.position + photonRay.direction * 0.001f;
        hit = GetNearestIntersection(photonRay);
    }

    return false;
}

[numthreads(32,1,1)]
void TraceCausticPhotons(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)max(0, _CausticPhotonAttemptCount))
    {
        return;
    }

    InterlockedAdd(_CausticPhotonMetadata[2], 1);

    if (_NumCausticTargetPairs <= 0)
    {
        return;
    }

    uint rngState = Hash(_CausticSeed ^ (_CausticFrameIndex * 2246822519u) ^ (id.x * 26699u));
    CausticTargetPair targetPair = _CausticTargetPairs[
        SelectCausticTargetPair(CausticSequenceSample(id.x, 0u))];
    Light light = _Lights[targetPair.lightIndex];

    float3 refractorPosition = float3(0.0f, 0.0f, 0.0f);
    float refractorRadius = 0.0f;
    int refractorIndex = -1;
    int refractorMeshIndex = -1;
    int refractorTriangleIndex = -1;
    float targetTriangleProbability = 1.0f;
    if (targetPair.refractorType == 0)
    {
        Sphere refractor = _Spheres[targetPair.refractorIndex];
        refractorPosition = refractor.position;
        refractorRadius = refractor.radius;
        refractorIndex = targetPair.refractorIndex;
    }
    else if (targetPair.refractorType == 1)
    {
        MeshInfo refractor = _Meshes[targetPair.refractorIndex];
        refractorPosition = (refractor.boundsMin + refractor.boundsMax) * 0.5f;
        refractorRadius = length(refractor.boundsMax - refractor.boundsMin) * 0.5f;
        refractorMeshIndex = targetPair.refractorIndex;
        CausticTargetTriangle targetTriangle = SelectCausticTargetTriangle(
            targetPair, CausticSequenceSample(id.x, 2u));
        refractorTriangleIndex = targetTriangle.triangleIndex;
        targetTriangleProbability = targetTriangle.selectionProbability;
    }
    else
    {
        float2 halfWaterSize = max(_WaterSize, float2(0.01f, 0.01f)) * 0.5f;
        float2 waterSample = float2(
            CausticDecorrelatedSample(id.x, 5u),
            CausticDecorrelatedSample(id.x, 6u));
        refractorPosition.xz = _WaterCenter.xz + (waterSample - 0.5f) * (halfWaterSize * 2.0f);
        refractorPosition.y = GetWaterWaveHeight(refractorPosition.xz);
    }

    bool isDirectionalSun = light.type == LightTypeSunTriangle;
    float3 emissionPosition = light.position;
    float emissionAreaScale = 1.0f;
    if (light.type == LightTypeTriangle || light.type == LightTypeSunTriangle)
    {
        float r1 = CausticSequenceSample(id.x, 3u);
        float r2 = CausticSequenceSample(id.x, 4u);
        if (r1 + r2 > 1.0f)
        {
            r1 = 1.0f - r1;
            r2 = 1.0f - r2;
        }
        emissionPosition += light.u * r1 + light.v * r2;
        emissionAreaScale = light.area;
    }

    // A virtual sun triangle is only an interface to the regular light buffer. Do not aim a
    // finite-emitter cone at the refractor: that pre-converges photons and masks IOR-dependent
    // focusing. Instead, sample an incident wavefront over the target's projected footprint.
    float3 directionalPhotonDirection = float3(0.0f, 0.0f, 0.0f);
    float directionalLaunchArea = 0.0f;
    if (isDirectionalSun)
    {
        float3 sunDirection = normalize(light.normal);
        float angularRadius = atan(sqrt(max(0.0f, light.area * 0.5f)) / max(1.0f, light.radius));
        directionalPhotonDirection = SampleCone(sunDirection, angularRadius, rngState);
        float3 launchTangent;
        float3 launchBitangent;
        CreateBasisFromNormal(directionalPhotonDirection, launchTangent, launchBitangent);

        float launchRadius = max(refractorRadius, 0.01f);
        if (refractorMeshIndex >= 0)
        {
            MeshInfo mesh = _Meshes[refractorMeshIndex];
            launchRadius = max(launchRadius, length(mesh.boundsMax - mesh.boundsMin) * 0.5f);
        }
        else if (targetPair.refractorType == 2)
        {
            launchRadius = max(launchRadius, length(_WaterSize) * 0.5f);
        }

        float2 launchSample = SampleDisk(rngState) * launchRadius;
        float3 launchCenter = refractorPosition + launchTangent * launchSample.x + launchBitangent * launchSample.y;
        // This only separates the wavefront from the target. It does not affect direction,
        // focus, or energy because the sun branch has no distance falloff.
        emissionPosition = launchCenter - directionalPhotonDirection * max(10.0f, launchRadius * 2.0f);
        directionalLaunchArea = PI * launchRadius * launchRadius;
    }

    float meshTargetInversePdf = 0.0f;
    if (refractorMeshIndex >= 0)
    {
        MeshTriangle targetTriangle = _Triangles[refractorTriangleIndex];
        float sampleU = CausticSequenceSample(id.x, 5u);
        float sampleV = CausticSequenceSample(id.x, 6u);
        if (sampleU + sampleV > 1.0f)
        {
            sampleU = 1.0f - sampleU;
            sampleV = 1.0f - sampleV;
        }

        float3 edgeU = targetTriangle.vertex1 - targetTriangle.vertex0;
        float3 edgeV = targetTriangle.vertex2 - targetTriangle.vertex0;
        refractorPosition = targetTriangle.vertex0 + edgeU * sampleU + edgeV * sampleV;
        float triangleArea = 0.5f * length(cross(edgeU, edgeV));
        float3 toTarget = refractorPosition - emissionPosition;
        float targetDistanceSquared = dot(toTarget, toTarget);
        if (triangleArea <= 1e-8f || targetDistanceSquared <= 1e-8f)
        {
            return;
        }

        float3 targetDirection = toTarget * rsqrt(targetDistanceSquared);
        float targetCosine = abs(dot(targetTriangle.normal, -targetDirection));
        if (targetCosine <= 1e-6f)
        {
            return;
        }

        meshTargetInversePdf = triangleArea * targetCosine
            / max(1e-8f, targetTriangleProbability * targetDistanceSquared);
    }

    float waterTargetInversePdf = 0.0f;
    if (targetPair.refractorType == 2)
    {
        float3 toTarget = refractorPosition - emissionPosition;
        float targetDistanceSquared = dot(toTarget, toTarget);
        if (targetDistanceSquared <= 1e-8f)
        {
            return;
        }

        float3 targetDirection = toTarget * rsqrt(targetDistanceSquared);
        float targetCosine = abs(dot(GetWaterNormal(refractorPosition.xz), -targetDirection));
        if (targetCosine <= 1e-6f)
        {
            return;
        }

        float targetArea = max(1e-6f, _WaterSize.x * _WaterSize.y);
        waterTargetInversePdf = targetArea * targetCosine / targetDistanceSquared;
    }

    float3 toRefractor = refractorPosition - emissionPosition;
    float distanceToRefractor = length(toRefractor);
    float emitterRadius = light.type == LightTypeSphere ? light.radius : 0.0f;
    if (distanceToRefractor <= (refractorIndex >= 0 ? refractorRadius : 0.0f) + emitterRadius + 0.001f)
    {
        return;
    }

    float3 coneAxis = toRefractor / distanceToRefractor;
    float sinThetaMax = refractorIndex >= 0 ? saturate(refractorRadius / distanceToRefractor) : 0.0f;
    float cosThetaMax = sqrt(max(0.0f, 1.0f - sinThetaMax * sinThetaMax));
    float cosTheta = lerp(1.0f, cosThetaMax, CausticSequenceSample(id.x, 7u));
    float sinTheta = sqrt(max(0.0f, 1.0f - cosTheta * cosTheta));
    float phi = 2.0f * PI * CausticSequenceSample(id.x, 8u);
    float3 tangent;
    float3 bitangent;
    CreateBasisFromNormal(coneAxis, tangent, bitangent);
    float3 photonDirection = isDirectionalSun
        ? directionalPhotonDirection
        : normalize(coneAxis * cosTheta + tangent * (cos(phi) * sinTheta) + bitangent * (sin(phi) * sinTheta));
    float emitterCosine = (light.type == LightTypeTriangle || light.type == LightTypeSunTriangle)
        ? dot(light.normal, photonDirection) : 1.0f;
    if (emitterCosine <= 0.0f)
    {
        return;
    }
    Ray photonRay = CreateRay(emissionPosition + photonDirection * max(0.001f, emitterRadius), photonDirection);
    RayHit refractorHit = CreateRayHit();
    if (isDirectionalSun)
    {
        refractorHit = GetNearestIntersection(photonRay);
    }
    else if (refractorIndex >= 0)
    {
        IntersectSphere(photonRay, refractorHit, _Spheres[refractorIndex], refractorIndex);
    }
    else if (refractorMeshIndex >= 0)
    {
        IntersectMeshBvh(photonRay, refractorHit, _Meshes[refractorMeshIndex]);
        float targetTolerance = max(0.002f, distanceToRefractor * 1e-5f);
        if (!DidHitSky(refractorHit)
            && length(refractorHit.position - refractorPosition) > targetTolerance)
        {
            return;
        }
    }
    else
    {
        IntersectWater(photonRay, refractorHit);
        // The sampled wave point is exact, while IntersectWater locates it with bounded marching
        // and refinement. A fixed position tolerance rejects photons in march-aligned bands.
        if (!IsWaterMaterial(refractorHit))
        {
            return;
        }
    }
    if (DidHitSky(refractorHit))
    {
        return;
    }

    float coneSolidAngle = 2.0f * PI * max(1e-6f, 1.0f - cosThetaMax);
    float inverseDirectionalPdf = refractorMeshIndex >= 0 ? meshTargetInversePdf
        : targetPair.refractorType == 2 ? waterTargetInversePdf
        : coneSolidAngle;
    if (light.type == LightTypeSunTriangle)
    {
        // The two virtual triangles represent one analytic emitter, not two independent
        // area emitters whose total power grows with their arbitrary placement distance.
        // The targeted cone PDF shrinks with distance squared; multiply by the stored virtual
        // disc distance squared so caustic flux matches the no-falloff direct-light model.
        emissionAreaScale = 0.5f * directionalLaunchArea;
        inverseDirectionalPdf = 1.0f;
    }
    float selectionScale = inverseDirectionalPdf * emissionAreaScale * emitterCosine
        / max(1e-8f, targetPair.selectionProbability);
    // Targeting only chooses an efficient emission direction. When water is a target, start at the
    // actual nearest boundary so an enclosing water volume is crossed before an interior refractor.
    RayHit firstTransportHit = refractorHit;
    if (targetPair.refractorType == 2)
    {
        firstTransportHit = GetNearestIntersection(photonRay);
    }
    CausticPhoton photon;
    if (!TraceCausticPhotonTransport(
        photonRay,
        firstTransportHit,
        light.emission * selectionScale,
        rngState,
        photon))
    {
        return;
    }

    uint writeIndex;
    InterlockedAdd(_CausticPhotonMetadata[0], 1, writeIndex);
    if (writeIndex < (uint)max(0, _CausticPhotonCapacity))
    {
        _CausticPhotons[writeIndex] = photon;
        InterlockedAdd(_CausticPhotonMetadata[3], 1);
    }
    else
    {
        InterlockedAdd(_CausticPhotonMetadata[1], 1);
    }
}

#endif

[numthreads(64,1,1)]
void BuildCausticGrid(uint3 id : SV_DispatchThreadID)
{
    uint storedPhotonCount = min(_CausticPhotonMetadata[0], (uint)max(0, _CausticPhotonCapacity));
    if (id.x >= storedPhotonCount)
    {
        return;
    }

    int3 cell = (int3)floor((_CausticPhotons[id.x].position - _CausticGridMin) / _CausticGridCellSize);
    if (any(cell < 0) || any(cell >= _CausticGridDimensions))
    {
        _CausticPhotonNext[id.x] = -1;
        InterlockedAdd(_CausticPhotonMetadata[4], 1);
        return;
    }

    int cellIndex = cell.x + _CausticGridDimensions.x * (cell.y + _CausticGridDimensions.y * cell.z);
    int previousHead;
    InterlockedExchange(_CausticGridCellHeads[cellIndex], (int)id.x, previousHead);
    _CausticPhotonNext[id.x] = previousHead;
    InterlockedAdd(_CausticPhotonMetadata[5], 1);
}
[numthreads(8,4,1)]
void CSCausticsDebug(uint3 id : SV_DispatchThreadID)
{
    uint width, height;
    Result.GetDimensions(width, height);
    if (id.x >= width || id.y >= height)
    {
        return;
    }

    float3 result = 0.0f;
    [loop]
    for (int i = 0; i < _NumberOfPasses; i++)
    {
        uint rngState = CreateRngState(id.xy, _SampleOffset + (uint)i);
        float2 pixelJitter = float2(rand(rngState), rand(rngState));
        // Keep the default sequence and pixel footprint bit-for-bit unchanged for existing
        // renders. Wider filters trade aliasing for deliberate cross-pixel blur.
        if (_SubpixelJitterScale != 1.0f)
        {
            pixelJitter = (pixelJitter - 0.5f) * _SubpixelJitterScale + 0.5f;
        }
        float2 uv = ((id.xy + pixelJitter) / float2(width, height)) * 2.0f - 1.0f;
        result += TraceVisibleCausticRadiance(CreateCameraRay(uv), rngState);
    }
    Result[id.xy] = float4(result / max(1, _NumberOfPasses), 1.0f);
}
