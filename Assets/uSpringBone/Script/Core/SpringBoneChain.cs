﻿using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Profiling;
using Unity.Entities;
using Unity.Transforms;
using Unity.Mathematics;
using Unity.Collections;
using Unity;
using Unity.Jobs;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

using BoneData = Es.uSpringBone.SpringBone.Data;
using ColliderData = Es.uSpringBone.SpringBoneCollider.Data;

namespace Es.uSpringBone
{
    /// <summary>
    /// One chain hierarchy of SpringBone.
    /// </summary>
    [ScriptExecutionOrder(0)]
    public class SpringBoneChain : MonoBehaviour
    {
        /// <summary>
        /// Information on the parent of RootBone.
        /// </summary>
        public struct ParentData
        {
            public float3 grobalPosition;
            public quaternion grobalRotation;
        }

        /// <summary>
        /// The main logic of SpringBone.
        /// Calculate the position of the child in the next frame and get rotation in that direction.
        /// </summary>
        [BurstCompile]
        public struct SpringBoneJob : IJob
        {
            public NativeArray<BoneData> boneData;
            [ReadOnly]
            public NativeArray<ParentData> parentData;
            [ReadOnly]
            public NativeArray<ColliderData> colliderData;
            [NativeDisableUnsafePtrRestriction]
            public unsafe BoneData * boneDataHeadPtr;
            [NativeDisableUnsafePtrRestriction]
            public unsafe ColliderData * colliderDataHeadPtr;
            public float dt;

            public unsafe void Execute()
            {
                boneDataHeadPtr = (BoneData*)boneData.GetUnsafePtr();
                colliderDataHeadPtr = (ColliderData*)colliderData.GetUnsafeReadOnlyPtr();

                float3 parentPosition = Vector3.zero;
                quaternion parentRotation = quaternion.identity;
                var sqrDt = dt * dt;
                var parentIndex = 0;

                for (int i = 0; i < boneData.Length; ++i)
                {
                    var boneDataPtr = (boneDataHeadPtr + i);

                    if (boneDataPtr -> IsRootBone)
                    {
                        parentPosition = parentData[parentIndex].grobalPosition;
                        parentRotation = parentData[parentIndex].grobalRotation;
                        ++parentIndex;
                    }

                    var localPosition = boneDataPtr -> localPosition;
                    var localRotation = boneDataPtr -> localRotation;
                    var grobalPosition = parentPosition + math.mul(parentRotation, localPosition);
                    var grobalRotation = math.mul(parentRotation, localRotation);

                    // calculate force
                    float3 force = math.mul(grobalRotation, (boneDataPtr -> boneAxis * boneDataPtr -> stiffnessForce)) / sqrDt;
                    force += (boneDataPtr -> previousEndpoint - boneDataPtr -> currentEndpoint) * boneDataPtr -> dragForce / sqrDt;
                    force += boneDataPtr -> springForce / sqrDt;

                    float3 temp = boneDataPtr -> currentEndpoint;
                    var dataTemp = boneData[i];

                    // calculate next endpoint position
                    dataTemp.currentEndpoint = (dataTemp.currentEndpoint - dataTemp.previousEndpoint) + dataTemp.currentEndpoint + (force * sqrDt);
                    dataTemp.currentEndpoint = (math.normalize(dataTemp.currentEndpoint - grobalPosition) * dataTemp.springLength) + grobalPosition;

                    // collision
                    for (int j = 0; j < colliderData.Length; j++)
                    {
                        var colliderDataPtr = (colliderDataHeadPtr + j);
                        if (math.distance(dataTemp.currentEndpoint, colliderDataPtr -> grobalPosition) <= (boneDataPtr -> radius + colliderDataPtr -> radius))
                        {
                            float3 normal = math.normalize(dataTemp.currentEndpoint - colliderDataPtr -> grobalPosition);
                            dataTemp.currentEndpoint = colliderDataPtr -> grobalPosition + (normal * (boneDataPtr -> radius + colliderDataPtr -> radius));
                            dataTemp.currentEndpoint = (math.normalize(dataTemp.currentEndpoint - grobalPosition) * dataTemp.springLength) + grobalPosition;
                        }
                    }

                    dataTemp.previousEndpoint = temp;

                    // calculate next rotation
                    float3 currentDirection = math.mul(parentRotation, boneDataPtr -> boneAxis);
                    quaternion targetRotation = Quaternion.FromToRotation(currentDirection, dataTemp.currentEndpoint - grobalPosition);

                    dataTemp.grobalPosition = parentPosition + math.mul(parentRotation, localPosition);
                    dataTemp.grobalRotation = math.mul(targetRotation, parentRotation);
                    parentPosition = dataTemp.grobalPosition;
                    parentRotation = dataTemp.grobalRotation;

                    // reset root.
                    if(boneDataPtr -> IsRootBone){
                        dataTemp.grobalPosition = grobalPosition;
                        dataTemp.grobalRotation = grobalRotation;
                        parentPosition = grobalPosition;
                        parentRotation = grobalRotation;
                    }

                    boneData[i] = dataTemp;
                }
            }
        }

        [SerializeField]
        private SpringBoneCollider[] colliders;

        SpringBone[] bones;
        Transform[] rootBoneParents;
        Transform cachedTransform;
        NativeArray<BoneData> boneData;
        NativeArray<ParentData> parentData;
        NativeArray<ColliderData> colliderData;
        BoneData[] boneDataTemp;
        ParentData[] parentDataTemp;
        ColliderData[] colliderDataTemp;
        SpringBoneJob job;
        JobHandle jobHandle;

        public SpringBone[] Bones { get { return bones; } }
        public NativeArray<BoneData> BoneData { get { return boneData; } }

        void Start()
        {
            cachedTransform = transform;
            bones = GetComponentsInChildren<SpringBone>();

            // Initialization of SpringBone.
            foreach (var bone in bones)
                bone.Initialize(this);

            // Cache parent's transform.
            rootBoneParents = bones.Where(t => t.data.IsRootBone)
                .Select(t => t.transform.parent)
                .ToArray();

            // Cancel parentage relationship.
            foreach (var bone in bones)
            {
                bone.transform.SetParent(null);
                // TODO: Hide in inspector.
            }


            // Memory allocation.
            boneDataTemp = new BoneData[bones.Length];
            parentDataTemp = new ParentData[rootBoneParents.Length];
            colliderDataTemp = new ColliderData[colliders.Length];
            boneData = new NativeArray<BoneData>(bones.Length, Allocator.Persistent);
            parentData = new NativeArray<ParentData>(rootBoneParents.Length, Allocator.Persistent);
            colliderData = new NativeArray<ColliderData>(colliders.Length, Allocator.Persistent);

            // Set data.
            SetBoneData();
            UpdateParentData();
            UpdateColliderData();

            // Register this component.
            SpringBoneJobScheduler.Instance.Register(this);

            // Create a job.
            job = new SpringBoneJob()
            {
                boneData = boneData,
                parentData = parentData,
                colliderData = colliderData
            };
        }

        void OnDestroy()
        {
            boneData.Dispose();
            colliderData.Dispose();
            parentData.Dispose();
        }

        /// <summary>
        /// Schedule the Job.
        /// </summary>
        public void ScheduleJob()
        {
            job.dt = Time.deltaTime;
            jobHandle = job.Schedule();
        }

        /// <summary>
        /// Wait for completion of Job.
        /// </summary>
        public void CompleteJob()
        {
            jobHandle.Complete();
        }

        /// <summary>
        /// Update the parent's information of RootBone.
        /// </summary>
        public void UpdateParentData()
        {
            Profiler.BeginSample("<> Update Parent Data");
            for (int i = 0; i < rootBoneParents.Length; ++i)
            {
                parentDataTemp[i].grobalPosition = rootBoneParents[i].position;
                parentDataTemp[i].grobalRotation = rootBoneParents[i].rotation;
            }
            parentData.CopyFrom(parentDataTemp);
            Profiler.EndSample();
        }

        /// <summary>
        /// Update collider's information.
        /// </summary>
        public void UpdateColliderData()
        {
            Profiler.BeginSample("<> Update Colliders");
            for (int i = 0; i < colliders.Length; ++i)
                colliderDataTemp[i] = colliders[i].data;
            colliderData.CopyFrom(colliderDataTemp);
            Profiler.EndSample();
        }

        /// <summary>
        /// Set information on Bone.
        /// </summary>
        private void SetBoneData()
        {
            for (int i = 0; i < bones.Length; ++i)
                boneDataTemp[i] = bones[i].data;
            boneData.CopyFrom(boneDataTemp);
        }
    }
}