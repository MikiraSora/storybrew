﻿using OpenTK;
using StorybrewCommon.Animations;
using StorybrewCommon.Storyboarding;
using StorybrewCommon.Storyboarding.CommandValues;
using System;
using System.Collections.Generic;

namespace StorybrewCommon.Storyboarding3d
{
    public class Object3d
    {
        private List<Object3d> children = new List<Object3d>();

        public readonly KeyframedValue<CommandColor> Coloring = new KeyframedValue<CommandColor>(InterpolatingFunctions.CommandColor, CommandColor.White);
        public readonly KeyframedValue<float> Opacity = new KeyframedValue<float>(InterpolatingFunctions.Float, 1);
        public StoryboardLayer Layer;

        public void Add(Object3d child)
        {
            children.Add(child);
        }

        public virtual Matrix4 WorldTransformAt(double time)
        {
            return Matrix4.Identity;
        }

        public void GenerateTreeSprite(StoryboardLayer layer)
        {
            GenerateSprite(Layer ?? layer);
            foreach (var child in children)
                child.GenerateTreeSprite(layer);
        }
        public void GenerateTreeKeyframes(double time, CameraState cameraState, Object3dState parent3dState)
        {
            var object3dState = new Object3dState(
                WorldTransformAt(time) * parent3dState.WorldTransform,
                Coloring.ValueAt(time) * parent3dState.Color,
                Opacity.ValueAt(time) * parent3dState.Opacity);

            GenerateKeyframes(time, cameraState, object3dState);
            foreach (var child in children)
                child.GenerateTreeKeyframes(time, cameraState, object3dState);
        }
        public void GenerateTreeCommands(Action<Action, OsbSprite> action = null)
        {
            GenerateCommands(action);
            foreach (var child in children)
                child.GenerateTreeCommands(action);
        }
        public void DoTree(Action<Object3d> action)
        {
            action(this);
            foreach (var child in children)
                child.DoTree(action);
        }
        public void DoTreeSprite(Action<OsbSprite> action)
        {
            var sprite = (this as HasOsbSprite)?.Sprite;
            if (sprite != null)
                action(sprite);
            foreach (var child in children)
                child.DoTreeSprite(action);
        }

        public virtual void GenerateSprite(StoryboardLayer layer)
        {
        }
        public virtual void GenerateKeyframes(double time, CameraState cameraState, Object3dState object3dState)
        {
        }
        public virtual void GenerateCommands(Action<Action, OsbSprite> action)
        {
        }
    }
}
