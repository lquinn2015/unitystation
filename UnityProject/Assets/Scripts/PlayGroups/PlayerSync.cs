﻿using System.Collections;
using System.Collections.Generic;
using Tilemaps;
using Tilemaps.Behaviours.Objects;
using Tilemaps.Scripts;
using UI;
using UnityEngine;
using UnityEngine.Networking;

namespace PlayGroup
{
	public struct PlayerState
	{
		public int MoveNumber;
		public Vector3 Position;
	}

	public struct PlayerAction
	{
		public int[] keyCodes;
	}

	public class PlayerSync : NetworkBehaviour
	{
		private bool canRegister = false;
		private HealthBehaviour healthBehaviorScript;

		//TODO: Remove the space damage coroutine when atmos is implemented
		private bool isApplyingSpaceDmg;

		private Vector2 lastDirection;

		private Matrix matrix => registerTile.Matrix;
		private Queue<PlayerAction> pendingActions;
		public PlayerMove playerMove;
		private PlayerScript playerScript;
		private PlayerSprites playerSprites;
		private PlayerState predictedState;

		private RegisterTile registerTile;
		private PlayerState serverState;

		[SyncVar] private PlayerState serverStateCache; //used to sync with new players

		public override void OnStartServer()
		{
			InitState();
			base.OnStartServer();
		}

		public override void OnStartClient()
		{
			StartCoroutine(WaitForLoad());
			base.OnStartClient();
		}

		private IEnumerator WaitForLoad()
		{
			yield return new WaitForEndOfFrame();
			if (serverStateCache.Position != Vector3.zero && !isLocalPlayer)
			{
				serverState = serverStateCache;
				transform.localPosition = RoundedPos(serverState.Position);
			}
			else
			{
				serverState = new PlayerState {MoveNumber = 0, Position = transform.localPosition};
				predictedState = new PlayerState {MoveNumber = 0, Position = transform.localPosition};
			}
			yield return new WaitForSeconds(2f);

		}

		private void InitState()
		{
			if (isServer)
			{
				Vector3Int position = Vector3Int.RoundToInt(transform.localPosition);
				serverState = new PlayerState {MoveNumber = 0, Position = position};
				serverStateCache = new PlayerState {MoveNumber = 0, Position = position};
			}
		}

		//Currently used to set the pos of a player that has just been dragged by another player
		[Command]
		public void CmdSetPositionFromReset(GameObject fromObj, GameObject otherPlayer, Vector3 setPos)
		{
			if (fromObj.GetComponent<PlayerSync>() == null) //Validation
			{
				return;
			}

			PlayerSync otherPlayerSync = otherPlayer.GetComponent<PlayerSync>();
			otherPlayerSync.SetPosition(setPos);
		}

		/// <summary>
		///     Manually set a player to a specific position
		/// </summary>
		/// <param name="pos">The new position to "teleport" player</param>
		[Server]
		public void SetPosition(Vector3 pos)
		{
			//TODO ^ check for an allowable type and other conditions to stop abuse of SetPosition
			Vector3Int roundedPos = Vector3Int.RoundToInt(pos);
			transform.localPosition = roundedPos;
			serverState = new PlayerState {MoveNumber = 0, Position = roundedPos};
			serverStateCache = new PlayerState {MoveNumber = 0, Position = roundedPos};
			predictedState = new PlayerState {MoveNumber = 0, Position = roundedPos};
			RpcSetPosition(roundedPos);
		}

		[ClientRpc]
		private void RpcSetPosition(Vector3 pos)
		{
			predictedState = new PlayerState {MoveNumber = 0, Position = pos};
			serverState = new PlayerState {MoveNumber = 0, Position = pos};
			transform.localPosition = pos;
		}

		private void Start()
		{
			if (isLocalPlayer)
			{
				pendingActions = new Queue<PlayerAction>();
				UpdatePredictedState();
			}
			playerScript = GetComponent<PlayerScript>();
			playerSprites = GetComponent<PlayerSprites>();
			healthBehaviorScript = GetComponent<HealthBehaviour>();
			registerTile = GetComponent<RegisterTile>();
		}


		private void Update()
		{
			if (isLocalPlayer && playerMove != null)
			{
				if (predictedState.Position == transform.localPosition && !playerMove.isGhost)
				{
					DoAction();
				}
				else if (predictedState.Position == playerScript.ghost.transform.localPosition && playerMove.isGhost)
				{
					DoAction();
				}
			}

			Synchronize();
		}

		private void RegisterObjects()
		{
			//Register playerpos in matrix
			registerTile.UpdatePosition();
		}

		private void DoAction()
		{
			PlayerAction action = playerMove.SendAction();
			if (action.keyCodes.Length != 0)
			{
				pendingActions.Enqueue(action);
				UpdatePredictedState();
				CmdAction(action);
			}
		}

		private void Synchronize()
		{
			if (isLocalPlayer && GameData.IsHeadlessServer)
			{
				return;
			}

			if (!playerMove.isGhost)
			{
				CheckSpaceWalk();

				PlayerState state = isLocalPlayer ? predictedState : serverState;
				transform.localPosition = Vector3.MoveTowards(transform.localPosition, state.Position, playerMove.speed * Time.deltaTime);

				//Check if we should still be displaying an ItemListTab and update it, if so.
				ControlTabs.CheckItemListTab();

				if (state.Position != transform.localPosition)
				{
					lastDirection = (state.Position - transform.localPosition).normalized;
				}

				//Registering
				if (registerTile.Position != Vector3Int.RoundToInt(state.Position))
				{
					RegisterObjects();
				}
			}
			else
			{
				PlayerState state = isLocalPlayer ? predictedState : serverState;
					playerScript.ghost.transform.localPosition =
						Vector3.MoveTowards(playerScript.ghost.transform.localPosition, state.Position, playerMove.speed * Time.deltaTime);
				
			}
		}

		[Command(channel = 0)]
		private void CmdAction(PlayerAction action)
		{
			serverState = NextState(serverState, action);
			//Do not cache the position if the player is a ghost
			//or else new players will sync the deadbody with the last pos
			//of the gost:
			if (!playerMove.isGhost)
			{
				serverStateCache = serverState;
			}
			RpcOnServerStateChange(serverState);
		}

		private void UpdatePredictedState()
		{
			predictedState = serverState;

			foreach (PlayerAction action in pendingActions)
			{
				predictedState = NextState(predictedState, action);
			}
		}

		private PlayerState NextState(PlayerState state, PlayerAction action)
		{
			return new PlayerState {MoveNumber = state.MoveNumber + 1, Position = playerMove.GetNextPosition(Vector3Int.RoundToInt(state.Position), action)};
		}

		[ClientRpc(channel = 0)]
		private void RpcOnServerStateChange(PlayerState newState)
		{
			serverState = newState;
			if (pendingActions != null)
			{
				while (pendingActions.Count > 0 && pendingActions.Count > predictedState.MoveNumber - serverState.MoveNumber)
				{
					pendingActions.Dequeue();
				}
				UpdatePredictedState();
			}
		}

		private Vector3 RoundedPos(Vector3 pos)
		{
			return new Vector3(Mathf.Round(pos.x), Mathf.Round(pos.y), pos.z);
		}

		private void CheckSpaceWalk()
		{
			if (matrix == null)
			{
				return;
			}
			Vector3Int pos = Vector3Int.RoundToInt(transform.localPosition);
			if (matrix.IsFloatingAt(pos))
			{
				Vector3Int newGoal = Vector3Int.RoundToInt(transform.localPosition + (Vector3) lastDirection);
				serverState.Position = newGoal;
				predictedState.Position = newGoal;
			}
			if (matrix.IsEmptyAt(pos) && !healthBehaviorScript.IsDead && CustomNetworkManager.Instance._isServer
			    && !isApplyingSpaceDmg)
			{
				//Hurting people in space even if they are next to the wall
				StartCoroutine(ApplyTempSpaceDamage());
				isApplyingSpaceDmg = true;
			}
		}

		//TODO: Remove this when atmos is implemented 
		//This prevents players drifting into space indefinitely 
		private IEnumerator ApplyTempSpaceDamage()
		{
			yield return new WaitForSeconds(1f);
			healthBehaviorScript.RpcApplyDamage(null, 5, DamageType.OXY, BodyPartType.HEAD);
			//No idea why there is an isServer catch on RpcApplyDamage, but will apply on server as well in mean time:
			healthBehaviorScript.ApplyDamage(null, 5, DamageType.OXY, BodyPartType.HEAD);
			isApplyingSpaceDmg = false;
		}
	}
}