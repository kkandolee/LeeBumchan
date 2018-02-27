using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using UnityEngine.SceneManagement;

using Bluespark.Common;
using Bluespark.Packet;
using Bluespark.Data;

public interface IGameEventHandler
{
	void ProcessGameEvent( EventId evtId );
}


public partial class SZGameManager : Singleton<SZGameManager>, IGameEventHandler
{
	public GameObject _ballBatting;
	public GameObject[] immortalObject;

	BaseGameMode _curGameMode;
	AsyncOperation _async;

	GameObject _mapAssetRoot;
	GameObject _mapWorksObj;
    
    PlayDataManager.GamePlayState _curState = PlayDataManager.GamePlayState.None;
    PlayDataManager.GamePlayState _nextState = PlayDataManager.GamePlayState.None;

	bool _bInitialized = false;

    PlayDataManager.GameMode _toChangeMode;
    string _toLoadMapName;

    int _warmUpModePlayCnt = 0;
	float _collectorWaitTime = 300.0f;
	float _ShowAppInfoTime = 12.0f;
	float _WaitAppInfoTime = 4.0f;

	bool _adminGamePause = false;
	bool _isResetBatterCount = false;

	#region WBC international scenes
	//@ WBC international scenes.
	[SerializeField]
    private List<string> _nameSceneWBCs = new List<string>();

    private bool _isModeInternational = false;
    #endregion WBC international scenes

	protected override void Awake()
	{
        try
        {
        SZLogger.Init();

		string path = Environment.GetEnvironmentVariable( "PATH" ) ?? string.Empty;
		path += ";" + Application.dataPath + @"\Plugins\";

		Environment.SetEnvironmentVariable( "PATH", path );

		base.Awake();

        Debug.Log("Success/base.Awake();");

		SZActionManager.UpdateActionMangers();

		if ( null != immortalObject )
		{
			foreach ( GameObject go in immortalObject )
			{
                if (null == go)
                {
                    Debug.LogError("@ERROR/(null == go)/immortalObject/");
                }

				DontDestroyOnLoad( go );    
			}
		}

        Debug.Log("Success/immortalObject");

		GameEventDispatcher.Instance.Init();

		Configuration.Instance.LoadConfiguration();
		BSDataManager.Instance.InitDataManager();

		ConfigurationPC.Instance.LoadConfiguration();       //@ 투구모드 설정들.

        ConfigurationSNP.Instance.LoadConfiguration();      //@ 투타모드 설정들.

        ConfigurationNasmo.Instance.LoadConfiguration();    //@ 나스모 설정들.

        ConfigurationEventTimeDelay.Instance.LoadConfiguration();   //@ 게임 이벤트 별 시간 딜레이 설정들.

        ConfigurationSpectator.Instance.LoadConfiguration();    //@ 관중 배치 설정.

        Debug.Log("Success/Configuration");

        Sensor.Instance.InitParameter(Configuration.Instance.Data.IsWaitGameStateIfConnectingPM);

#if USE_TTS
        TTS.Instance.Initialize();	
#endif

        FrameTargetHelper.SetFrameTarget(Configuration.Instance.Data.frameRateTarget);

        // load movie player
        MoviePlayer.Instance.Init();

		BaseGameMode.TimerForRollback.Init();           //@ 되돌리기 용도 초기화.

        Capturer.NasmoInit();

        SimpleProfiler.Init(Configuration.Instance.Data.IsUseSimpleProfiler);

        QualitySettings.SetQualityLevel(Configuration.Instance.Data.QualityLevel, true);

        Debug.logger.logEnabled = Configuration.Instance.Data.IsOutputLogAll;

        if (true == Debug.logger.logEnabled && true == Configuration.Instance.Data.IsDebugLogView)
        {
            gameObject.AddComponent<LogGUIViewer>();
        }

        if (true == Configuration.Instance.Data.IsUseSimpleProfiler && true == Application.isEditor)
        {
            MemoryProfiler.GetThis.Init();
        }

        StartWarmUpMode();

        Debug.Log("Success/Awake/SZGameManager");

        } // try
        catch (Exception e)
        {
            Debug.LogError(e);
        }
    } // protected override void Awake()

    private void Start()
    {
        SZActionManager.InitPostActionManager();

        GameEventDelayTimer.Init(this);
        List<TimeDelayGameEvent> listTimeDelays = ConfigurationEventTimeDelay.Instance.Data.timeDelayGameEvents;

        for(int i = 0; i < listTimeDelays.Count(); ++i)
        {
            TimeDelayGameEvent timeDelay = listTimeDelays[i];
            GameEventDelayTimer.NewAdd(timeDelay.GameState, timeDelay.TimeDelay);
        }
    }

    void OnApplicationQuit()
    {
        SZLogger.OnDestroy();
        Capturer.NasmoDestroy();
    }    

    public void StartWarmUpMode()
    {        
        SZModeConfigRecord modeConfigRecord = BSDataManager.Instance.FindSZModeConfig( SZGameDataManager.Instance.SZPlatformType, SZGameDataManager.Instance.SZModeType, SZGameDataManager.Instance.SZMiniType );
        if (null == modeConfigRecord)
        {
            Debug.Log( "mode config is not found ");
            SZUIManager.Instance.SetVisibleUI<LoadingUI>( true );
            return;
        }            
        
        StadiumRecord stadiumRecord = BSDataManager.Instance.Get().GetStadiumTable().Find( modeConfigRecord.bc_stadium_id );
        if (stadiumRecord == null)
        {
            Debug.Log( "Warmup mode stadium not found, id: " + modeConfigRecord.bc_stadium_id );
            SZUIManager.Instance.SetVisibleUI<LoadingUI>( true );
            return;
        }

        PlayDataManager.Instance.ClearAllTeamData();
        PlayDataManager.Instance.ClearPlayerData();
        StartGameMode( PlayDataManager.GameMode.WarmUp, stadiumRecord.level_name );
    }


    PlayDataManager.GameMode _reservedMode = PlayDataManager.GameMode.None;
    string _reservedMapName;
	public void StartGameMode( PlayDataManager.GameMode mode, string mapName )
	{
        // in loading state, reserve next game mode
        if (PlayDataManager.GamePlayState.GameLoading == _curState)
        {
            Debug.Log( "============ reserved game mode!!!!" );
            _reservedMode = mode;
            _reservedMapName = mapName;
            return;
        }

        _isModeInternational = IsContainsSceneWBC( mapName );

        InternalStartGameMode( mode, mapName );
	}

    void InternalStartGameMode( PlayDataManager.GameMode mode, string mapName )
    {
        Utils.CombineString( "+++++++++++++ StartGameMode ", mode.ToString(), " ", mapName );
        Debug.Log( Utils.GetCombinedString() );

        _toChangeMode = mode;
        _toLoadMapName = mapName;

        if (mode != PlayDataManager.GameMode.WarmUp)
        {
            MoviePlayer.Instance.PlayCautionMovie( ChangeGameMode, StartLoading );
            ResetWarmUpModePlayCount();
        }
        else
        {
            ChangeGameMode();
#if NO_WARMUP
			SZUIManager.Instance.SetVisibleUI<LoadingUI>( true );
			Init();
#else
            StartLoading();
#endif			
        }
    }

    void ChangeGameMode()
    {
        DestroyGameMode();

#if REFACTORING_AI
		if (SZGameManagerNew.Instance.IsPlaying())
			SZGameManagerNew.Instance.EndGameMode();
#endif
        _curGameMode = CreateGameMode( _toChangeMode );
        if (null == _curGameMode)
        {
            Debug.Log("### mode is null ");
            return;
        }
    }

    void StartLoading()
    {
        ChangeGameState( PlayDataManager.GamePlayState.GameLoading );
    }

	BaseGameMode CreateGameMode( PlayDataManager.GameMode mode )
	{
		Debug.Log( "=============== CreateGameMode " + mode );

        Sensor.Instance.InitSensor(mode);

        switch ( mode )
		{
			case PlayDataManager.GameMode.NormalGame:
                return new NormalGame11Mode();

			case PlayDataManager.GameMode.BattingChallenge:
                return new BattingChallengeMode();

            case PlayDataManager.GameMode.BattingChallengeTeam:
                return new BattingChallengeMode( true );

            case PlayDataManager.GameMode.Training:
                return new TrainingGameMode();

            case PlayDataManager.GameMode.WarmUp:
                return new WarmUpMode();

            case PlayDataManager.GameMode.NormalGame111:
                return new NormalGame111Mode();

            case PlayDataManager.GameMode.PitchingChallenge:
                return new PitchingChallengeMode();
			
			case PlayDataManager.GameMode.SwitchNormalPitching:
				return new SwitchNormalPitchingMode();
        }

		return null;
	}


	public void DestroyGameMode()
	{
        _curState = PlayDataManager.GamePlayState.None;
        _nextState = PlayDataManager.GamePlayState.None;

        _bPlayingCutScene = false;

        if (null != _curGameMode)
            PhysicHelper.Instance.ResultDelegate -= _curGameMode.SimulationResult;

        SZCameraManager.Instance.StopPlayingActors();

		// destroy mode
		if ( null != _curGameMode )
		{
			_curGameMode.Destroy();
			_curGameMode = null;
		}

// 		Scene scene = SceneManager.GetActiveScene();
// 
// 		SceneManager.UnloadScene( scene.name );

		// unload stadium
		if ( null != _mapAssetRoot )
		{
			GameObject.DestroyImmediate( _mapAssetRoot );
			_mapAssetRoot = null;
		}

		if ( null != _mapWorksObj )
		{
			GameObject.DestroyImmediate( _mapWorksObj );
			_mapWorksObj = null;
		}

		// destroy camear components
		Camera[] cameras = SZCameraManager.Instance.gameObject.GetComponentsInChildren<Camera>( true );
		foreach (Camera cam in cameras)
		{
			Utils.DeleteCameraComponent( cam.gameObject );
		}

//		StartCoroutine( UnLoadLoadedStadium() );

        // stop all action manager
        SZActionManager.StopAllManager();

		// clear character & character data
		PlayDataManager.Instance.ClearPlayerData();
		CharacterLoader characterLoader = this.gameObject.GetComponent<CharacterLoader>();
		if (null != characterLoader)
		{
			characterLoader.ClearAll();
		}
        
		SpectatorManager.Instance.DeleteSpectator();

        // clear all action in loom, have to check side effect more
        Loom loom = FindObjectOfType<Loom>();
        if (loom)
            loom.ClearAllAction();

		Resources.UnloadUnusedAssets();
        GC.Collect();
	}

	/// <summary>
	/// 시스템모니터에서 호출.
	/// </summary>
	/// <param name="GameModeId">(byte)settingData.currentSelectModeRecord.id</param>
	/// <param name="GameSettingData">GenerateGameData()</param>
	/// <param name="EntryPlayerDataList">GenerateUserDataForGameClient()</param>
	/// <returns></returns>
	public BSResultCode EnterGame( byte GameModeId, OptionData GameSettingData, List<UserData> EntryPlayUserList )
	{
        if ( SZGameManager.Instance.GetGameModeType() != PlayDataManager.GameMode.WarmUp &&
			SZGameManager.Instance.GetCurGameState() == PlayDataManager.GamePlayState.GameEntry )
		{
			Debug.Log( "invalid gamestate, state: " + SZGameManager.Instance.GetCurGameState() );

			return BSResultCode.Game_InvalidGameState;
		}

		GameModeRecord gameModeRecord = BSDataManager.Instance.FindGameMode( GameModeId );
		if ( gameModeRecord == null )
		{
			Debug.Log( "GameMode not found, id: " + GameModeId );
			return BSResultCode.Game_GameDataNotFound;
		}

		string levelName = string.Empty;
		PlayDataManager.GameMode gameMode = PlayDataManager.GameMode.NormalGame;

		PlayDataManager.Instance.SetGameSettingData( (OptionData)GameSettingData.Clone() );

		StadiumRecord stadiumRecord = null;

		if (GameModeType.PitchingChallenge != gameModeRecord.type)	//@ 피칭 모드는 무작위 스타디움 로드.
		{
			stadiumRecord = BSDataManager.Instance.Get().GetStadiumTable().Find(GameSettingData.currentStadiumID);
			if (stadiumRecord == null)
			{
				Debug.Log("Stadium not found, id: " + GameSettingData.currentStadiumID);
				return BSResultCode.Game_GameDataNotFound;
			}
		}

		switch ( gameModeRecord.type )
		{
			case GameModeType.NormalGame:
				{
					if ( PlayDataManager.Instance.UseNightStadium )
					{
						levelName = stadiumRecord.night_level_name;
					}
					else
					{
						levelName = stadiumRecord.level_name;
					}

					PlayDataManager.Instance.SetStadiumID( Convert.ToInt16( GameSettingData.currentStadiumID ) );
					PlayDataManager.Instance.SetMaxInning( GameSettingData.currentSelectInning );
					PlayDataManager.Instance.SetStadiumName( stadiumRecord.name_K );
					PlayDataManager.Instance.SetStadiumSpectatorName( stadiumRecord.spectatorlist_name );

                    gameMode = PlayDataManager.GameMode.NormalGame;
				}
				break;

			case GameModeType.Training:
				{
					levelName = stadiumRecord.level_name;
					gameMode = PlayDataManager.GameMode.Training;
				}
				break;

            case GameModeType.BattingChallenge:
                {
                    levelName = stadiumRecord.level_name;
                    PlayDataManager.Instance.SetStadiumSpectatorName( stadiumRecord.spectatorlist_name );

                    if (GameSettingData.bcTeamMode)
                        gameMode = PlayDataManager.GameMode.BattingChallengeTeam;
                    else
                        gameMode = PlayDataManager.GameMode.BattingChallenge;
                }
                break;

			case GameModeType.NormalGame111:
				{
					if ( PlayDataManager.Instance.UseNightStadium )
					{
						levelName = stadiumRecord.night_level_name;
					}
					else
					{
						levelName = stadiumRecord.level_name;
					}

					PlayDataManager.Instance.SetStadiumID( Convert.ToInt16( GameSettingData.currentStadiumID ) );
					PlayDataManager.Instance.SetMaxInning( GameSettingData.currentSelectInning );
					PlayDataManager.Instance.SetStadiumName( stadiumRecord.name_K );
					PlayDataManager.Instance.SetStadiumSpectatorName( stadiumRecord.spectatorlist_name );

					gameMode = PlayDataManager.GameMode.NormalGame111;
				}
				break;

			case GameModeType.PitchingChallenge:
				{
					int idStadiumForTest = ConfigurationPC.instance.Data.PCStadiumIdForced;
					if (-1 == idStadiumForTest)
					{
						//@랜덤 스테디움 한개 추출.
						stadiumRecord = BSDataManager.Instance.GetSZModeStadiumbyRandom();
					}
					else
					{
						stadiumRecord = BSDataManager.Instance.GetSZModeStadium(idStadiumForTest);
					}
					
					levelName = stadiumRecord.level_name;

					PlayDataManager.Instance.SetStadiumID((short)stadiumRecord.id);
					PlayDataManager.Instance.SetStadiumSpectatorName( stadiumRecord.spectatorlist_name );
					gameMode = PlayDataManager.GameMode.PitchingChallenge;
				}
				break;

			case GameModeType.SwitchNormalPitching:
				//투타모드
				//임시로 Normal모드 코드 복사.
				{
					if ( PlayDataManager.Instance.UseNightStadium )
					{
						levelName = stadiumRecord.night_level_name;
					}
					else
					{
						levelName = stadiumRecord.level_name;
					}

					PlayDataManager.Instance.SetStadiumID( Convert.ToInt16( GameSettingData.currentStadiumID ) );
					PlayDataManager.Instance.SetMaxInning( GameSettingData.currentSelectInning );
					PlayDataManager.Instance.SetStadiumName( stadiumRecord.name_K );
					PlayDataManager.Instance.SetStadiumSpectatorName( stadiumRecord.spectatorlist_name );

					gameMode = PlayDataManager.GameMode.SwitchNormalPitching;
				}

				break;

			default:
				{
					Debug.Log( "invalid GameMode, GameMode: " + gameModeRecord.type );
					return BSResultCode.Game_InvalidGameMode;
				}
		}

		PlayDataManager.Instance.SetDefenseLevel( GameSettingData.defenseLevel );
        PlayDataManager.Instance.ClearAllTeamData();
        PlayDataManager.Instance.ClearPlayerData();

		Sensor.Instance.Clear_PM_LaunchBallCount();

		SZGameManager.Instance.StartGameMode( gameMode, levelName );

		if ( PlayDataManager.Instance.IsTournament && PlayDataManager.Instance.IsSoloTournament )
		{
			string botName = string.Empty;

			TeamRecord record = BSDataManager.Instance.FindSZModeTeam( PlayDataManager.Instance.SelectedAwayTeamID );
			if ( record != null )
			{
				//botName = record.name_K + " 봇";
                botName = record.name_K + " " + BSDataManager.Instance.FindText("STR_System_Bot");
			}
			else
			{
				Debug.LogFormat( "TeamRecord not found, id: {0}", PlayDataManager.Instance.SelectedAwayTeamID );
			}

			UserData tempBot = new UserData( 1, botName, PlayerGender.Male, 1, TeamType.Away );

			tempBot.SetTeamData( TeamType.Away, PlayDataManager.Instance.SelectedAwayTeamID );

			EntryPlayUserList.Insert( 0, tempBot );
		}

		if (gameModeRecord.type == GameModeType.SwitchNormalPitching)
		{
			for (int i = 0; i < EntryPlayUserList.Count; ++i)
			{
				UserData current = EntryPlayUserList[i];

				PlayDataManager.Instance.AddUserData(current);
			}
		}
		else
		{
            if (gameModeRecord.type == GameModeType.PitchingChallenge)
            {
                UserData botOffenseData = new UserData(int.MaxValue, BasePitchingMode.Settings.NameBot, BasePitchingMode.Settings.GenderBot, 1);
                botOffenseData.SetTeamData(BasePitchingMode.Settings.TeamTypeBot, BasePitchingMode.Settings.TeamIDBot);

                EntryPlayUserList.Insert(0, botOffenseData);
            }

            bool bSoloPlyaerTeam = false;
            if ((GameModeType.BattingChallenge == gameModeRecord.type && false == GameSettingData.bcTeamMode)
                /*|| GameModeType.PitchingChallenge == gameModeRecord.type*/)
                bSoloPlyaerTeam = true;

            int cnt = EntryPlayUserList.Count;
            for (int i = 0; i < cnt; i++)
            {
                UserData userData = EntryPlayUserList[i];
                PlayDataManager.Instance.AddUserData( userData, bSoloPlyaerTeam );

				long teamID = userData.teamRecord.id;

                if (BSDataManager.Instance.IsCustomTeam( (int)teamID ) && CustomTeamManager.Instance.IsExistTeamInfo( teamID ))
                {
					CustomTeamManager.Instance.UpdateTeamNameTexture( teamID );
                }
            }

        }

		return BSResultCode.NoError;
	}

	public BSResultCode EnterGame( List<UserData> userList )
	{
		PlayDataManager.GameMode gameModeType = GetGameModeType();
		//PlayDataManager.GamePlayState gamePlayState = GetCurGameState();

		if ( userList == null || userList.Count <= 0 )
		{
			Debug.LogErrorFormat( "EnterGame, userlist IsNullOrEmpty" );

			return BSResultCode.Game_EnterFailed;
		}

		if ( PlayDataManager.instance.IsInProgressPrepareAction( PlayDataManager.PrepareActionLeave ) )
		{
			Debug.LogFormat( "EnterGame, invalid status, in progress" );

			return BSResultCode.Game_EnterFailed_InProgress;
		}

		if ( PlayDataManager.Instance.IsTournament )
		{
			Debug.LogFormat( "EnterGame, invalid gamemode, is tournament" );

			return BSResultCode.Game_EnterFailed;
		}


		BSResultCode resultCode = BSResultCode.NoError;

		List<TeamData> teamDataList = PlayDataManager.instance.GetTeamDataList();
		//int teamDataCnt = teamDataList.Count;
		int userCnt = userList.Count;

		switch ( gameModeType )
		{
			case PlayDataManager.GameMode.NormalGame:
			case PlayDataManager.GameMode.NormalGame111:
			case PlayDataManager.GameMode.BattingChallengeTeam:
				{
					int numberofTeamUser_Max = ( gameModeType == PlayDataManager.GameMode.NormalGame111 ) ? ( BSDataManager.NumberofUser - 2 ) : ( BSDataManager.NumberofUser - 1 );

					PlayDataManager.CompareFunc func = ( teamType, teamUserCnt, count ) => {
						int prepareEnterUserCount = PlayDataManager.Instance.GetPrepareEnterUserCount( teamType );
						if ( teamUserCnt + prepareEnterUserCount + count > numberofTeamUser_Max )
						{
							Debug.LogFormat( "EnterGame, threshold has been exceeded, GameMode: {0}, TeamType: {1}, number of user: {2}/{3}", gameModeType, teamType, teamUserCnt + count, numberofTeamUser_Max );
							return false;
						}

						return true;
					};

					if ( !PlayDataManager.Instance.ValidationCheckTeamUserCount( userList, func ) )
					{
						resultCode = BSResultCode.Game_EnterFailed_User_Overflow;
					}
					else
					{
						int awayUserCnt = 0, homeUserCnt = 0;

						for ( int i = 0; i < userCnt; ++i )
						{
							UserData userData = userList[i];
							TeamType teamType = userData.teamType;

							PlayDataManager.OutUserData data = null;
							if ( !userData.IsGuest && PlayDataManager.Instance.GetOutUserData( userData.Prop.UserNo, ref data ) )
							{
								if ( userData.teamType == data.TeamType )
									continue;

								Debug.LogErrorFormat( "EnterGame, TeamType different, UserName: {0}, TeamType: {1}/{2}", userData.userName, userData.teamType, data.TeamType );

								resultCode = BSResultCode.Game_EnterFailed_User_DifferentTeam;
								break;
							}

							if ( teamType == TeamType.Away ) ++awayUserCnt;
							else if ( teamType == TeamType.Home ) ++homeUserCnt;
						}

						if ( resultCode == BSResultCode.NoError && gameModeType == PlayDataManager.GameMode.BattingChallengeTeam )
						{
							awayUserCnt += PlayDataManager.Instance.GetPrepareEnterUserCount( TeamType.Away );
							homeUserCnt += PlayDataManager.Instance.GetPrepareEnterUserCount( TeamType.Home );
							if ( awayUserCnt != homeUserCnt )
							{
								resultCode = BSResultCode.Game_EnterFailed_TeamUser_Count;
							}
						}
					}
				}
				break;

			case PlayDataManager.GameMode.BattingChallenge:
			case PlayDataManager.GameMode.PitchingChallenge:
            case PlayDataManager.GameMode.SwitchNormalPitching:
                {
					if ( userCnt + (teamDataList.Count - 1) >= BSDataManager.NumberofUser )
					{
						Debug.LogFormat( "EnterGame, threshold has been exceeded, GameMode: {0}, number of user: {1} + {2}/{3}", gameModeType, (teamDataList.Count - 1), userCnt, BSDataManager.NumberofUser );

						resultCode = BSResultCode.Game_EnterFailed_User_Overflow;
					}
				}
				break;

			default:
				{
					Debug.LogErrorFormat( "EnterGame, invalid gamemode, GameMode: {0}", gameModeType );

					resultCode = BSResultCode.Game_InvalidGameMode;
				}
				break;
		}

		if ( resultCode == BSResultCode.NoError )
		{
			for ( int i = 0; i < userCnt; i++ )
			{
				PlayDataManager.Instance.AddPrepareEnterGame( userList[i] );
			}
		}

		return resultCode;
	}

	public BSResultCode LeaveGame( List<UserData> userList )
	{
		PlayDataManager.GameMode gameModeType = GetGameModeType();
		//PlayDataManager.GamePlayState gamePlayState = GetCurGameState();

		if ( userList == null || userList.Count <= 0 )
		{
			Debug.LogErrorFormat( "EnterGame, userlist IsNullOrEmpty" );

			return BSResultCode.Game_LeaveFailed;
		}

		if ( PlayDataManager.instance.IsInProgressPrepareAction( PlayDataManager.PrepareActionEnter ) )
		{
			Debug.LogFormat( "LeaveGame, invalid status, in progress" );

			return BSResultCode.Game_LeaveFailed_InProgress;
		}

		if ( PlayDataManager.Instance.IsTournament )
		{
			Debug.LogFormat( "LeaveGame, invalid gamemode, is tournament" );

			return BSResultCode.Game_LeaveFailed;
		}


		BSResultCode resultCode = BSResultCode.NoError;

		List<TeamData> teamDataList = PlayDataManager.instance.GetTeamDataList();
		int teamDataCnt = teamDataList.Count;
		int userCnt = userList.Count;

		switch ( gameModeType )
		{
			case PlayDataManager.GameMode.NormalGame:
			case PlayDataManager.GameMode.NormalGame111:
			case PlayDataManager.GameMode.BattingChallengeTeam:
				{
					PlayDataManager.CompareFunc func = ( teamType, teamUserCnt, count ) => {
						int prepareLeaveUserCount = PlayDataManager.Instance.GetPrepareLeaveUserCount( teamType );
						if ( teamUserCnt - prepareLeaveUserCount - count < PlayDataManager.NumberofTeamUser_Min )
						{
							Debug.LogFormat( "LeaveGame, threshold has been exceeded, GameMode: {0}, TeamType: {1}, number of user: {2}/{3}", gameModeType, teamType, count, teamUserCnt );
							return false;
						}

						return true;
					};

					if ( !PlayDataManager.Instance.ValidationCheckTeamUserCount( userList, func ) )
					{
						resultCode = BSResultCode.Game_LeaveFailed_User_Underflow;
					}
					else
					{
						if ( gameModeType == PlayDataManager.GameMode.BattingChallengeTeam )
						{
							BaseGameMode gameMode = GetGameMode();

							int awayUserCnt = 0, homeUserCnt = 0;

							for ( int i = 0; i < userCnt; ++i )
							{
								UserData userData = userList[i];
								TeamType teamType = userData.teamType;

								PlayerStatus status = gameMode.GetPlayerStatus( userData.uid );
								if ( status != PlayerStatus.Idle )
								{
									resultCode = BSResultCode.Game_LeaveFailed_Invalid_PlayerStatus;
									break;
								}

								if ( teamType == TeamType.Away ) ++awayUserCnt;
								else if ( teamType == TeamType.Home ) ++homeUserCnt;
							}

							if ( resultCode == BSResultCode.NoError )
							{
								awayUserCnt += PlayDataManager.Instance.GetPrepareLeaveUserCount( TeamType.Away );
								homeUserCnt += PlayDataManager.Instance.GetPrepareLeaveUserCount( TeamType.Home );
								if ( awayUserCnt != homeUserCnt )
								{
									resultCode = BSResultCode.Game_LeaveFailed_TeamUser_Count;
								}
							}
						}
					}
				}
				break;

			case PlayDataManager.GameMode.BattingChallenge:
			case PlayDataManager.GameMode.PitchingChallenge:
            case PlayDataManager.GameMode.SwitchNormalPitching:
                {
					if ( !ValidationCheckPlayerStatus( userList, PlayerStatus.Idle ) )
					{
						resultCode = BSResultCode.Game_LeaveFailed_Invalid_PlayerStatus;
					}

					if ( resultCode == BSResultCode.NoError && ( (teamDataCnt - 1) - userCnt ) < PlayDataManager.NumberofTeamUser_Min )
					{
						Debug.LogFormat("LeaveGame, threshold has been exceeded, GameMode: {0}, number of user: {1}/{2}", gameModeType, teamDataCnt, userCnt);

						resultCode = BSResultCode.Game_LeaveFailed_User_Underflow;
					}
				}
				break;

			default:
				{
					Debug.LogErrorFormat( "LeaveGame, invalid gamemode, GameMode: {0}", gameModeType );

					resultCode = BSResultCode.Game_InvalidGameMode;
				}
				break;
		}

		if ( resultCode == BSResultCode.NoError )
		{
			for ( int i = 0; i < userCnt; i++ )
			{
				PlayDataManager.Instance.AddPrepareLeaveGame( userList[i] );
			}
		}

		return resultCode;
	}

    public bool IsModeInternational
    {
        get { return _isModeInternational; }
    }

    public bool IsContainsSceneWBC(string nameScene)
    {
        return false == _nameSceneWBCs.IsNullOrEmpty() 
            && false == _nameSceneWBCs.FirstOrDefault(s=> { return false == s.IsNullOrEmpty() && s.IsSame(nameScene, true); } ).IsNullOrEmpty();
    }

	public PlayDataManager.GameMode GetGameModeType()
	{
		if ( null == _curGameMode )
			return PlayDataManager.GameMode.None;

		return _curGameMode.GetModeType();
	}

    public bool IsBattingChallengeMode()
    {
        if ( null == _curGameMode )
            return false;

        PlayDataManager.GameMode gameMode = _curGameMode.GetModeType();

        if (PlayDataManager.GameMode.BattingChallenge == gameMode ||
            PlayDataManager.GameMode.BattingChallengeTeam == gameMode ||
            PlayDataManager.GameMode.WarmUp == gameMode)
            return true;

        return false;
    }

	public bool IsPitchingMode()
	{
		if ( null == _curGameMode )
			return false;

		PlayDataManager.GameMode gameMode = _curGameMode.GetModeType();
		
        if (PlayDataManager.GameMode.PitchingChallenge == gameMode
            || true == SwitchNormalPitchingMode.IsModeCurrentPitching)
        {
            return true;
        }

		return false;
	}

    public bool IsTrainingMode()
    {
        if (null == _curGameMode)
            return false;

        return PlayDataManager.GameMode.Training == _curGameMode.GetModeType();
    }

    public bool IsPitchingModeAsSwitchMode()
	{
		return SwitchNormalPitchingMode.IsModeCurrentPitching;
	}

	public bool IsPitchingChallengeMode()
	{
		if (null == _curGameMode)
			return false;

		PlayDataManager.GameMode gameMode = _curGameMode.GetModeType();

		return (PlayDataManager.GameMode.PitchingChallenge == gameMode);
	}

	public bool IsSwitchNormalPitchingMode()
    {
        if (null == _curGameMode)
            return false;

        return PlayDataManager.GameMode.SwitchNormalPitching == _curGameMode.GetModeType();
    }

    public BaseGameMode GetGameMode()
	{
		return _curGameMode;
	}


	public IEnumerator MissionPop(List<string> missionstringlist, string username)
	{
		if (missionstringlist.Count == 0)
		{
			Debug.Log( "Missionstringlist is 0" );
			yield return null;
		}
			

		if (username == null)
		{
			Debug.Log( "MissionUserName is null" );
			yield return null;
		}

		List<string> missionlist = new List<string>();

		for(int i = 0; i < missionstringlist.Count; i++)
		{
			missionlist.Add( missionstringlist[i] );
		}
		
		if (GetGameMode().GetHUDUI().GetHomeRunUI().activeSelf == true)
		{
			float delaytime = 0.0f;
			delaytime = GetGameMode().GetHUDUI().GetHomeRunUI().GetComponent<TweenScale>().delay + GetGameMode().GetHUDUI().GetHomeRunUI().GetComponent<TweenScale>().duration + 6.0f;

			yield return new WaitForSeconds(delaytime);

			SZGameDataManager.Instance.ClearCompleteMissionList();
			GetGameMode().GetHUDUI().MissionPopupOn( missionlist, username );
		}
		else
		{
			GetGameMode().GetHUDUI().MissionPopupOn( missionlist, username );
		}		
	}
	

	public void ForceHideMissionPopup()
	{
		GetGameMode().GetHUDUI().ForceCloseMissionPopup();
	}


	bool _bLoadStadiumFail = false;
    const int _tryLoadCnt = 3;
    IEnumerator LoadStadium()
    {
        //SimpleProfiler.Start("LoadStadium", true);

        _bLoadStadiumFail = true;
        int tryCnt = 0;
        while (_bLoadStadiumFail && (++tryCnt <= _tryLoadCnt))
        {
            if (1 < tryCnt)
                Debug.Log( "Retried load stadium " + (tryCnt-1) );

            _bLoadStadiumFail = false;

            // wiat for movie player stop
            yield return new WaitForSeconds( 2f );
            yield return StartCoroutine( LoadStadium( _toLoadMapName ) );
        }

        if (_bLoadStadiumFail)
        {
            // to do
            // to add load stadium fail process
        }
		       
		// SimpleProfiler.End("LoadStadium");

        SimpleProfiler.PrintResults( true );
    }

	string _lastLoadedStadium;
	IEnumerator LoadStadium( string sceneName )
	{
        Debug.Log( "=== LoadStadium:" + sceneName + "/Time:" + Time.time + "/GameMode:" + GetGameModeType() );

		if (string.IsNullOrEmpty( sceneName ))
			yield break;        

        if (false == Application.CanStreamedLevelBeLoaded( sceneName ))
        {
            Debug.LogError( "@ERROR/=== LoadStadum unexistanced scene " + sceneName );
            yield break;
        }

        _async = SceneManager.LoadSceneAsync( sceneName );
        
		if (null == _async)
        {
			Debug.LogError( "=== LoadStadium Fail!!(LoadLevelAsync is null) " + sceneName );
            _bLoadStadiumFail = true;
            yield break;
        }

		_async.allowSceneActivation = false;

		while (_async.progress < 0.9f)
		{
			yield return new WaitForEndOfFrame();
		}

		_async.allowSceneActivation = true;

		while (false == _async.isDone)
		{
			yield return new WaitForEndOfFrame();
		}

		_lastLoadedStadium = sceneName;

        PreInit();

		_async = Resources.UnloadUnusedAssets();
		while (false == _async.isDone)
		{
			yield return new WaitForEndOfFrame();
		}

        Debug.Log( "=== LoadStadium Load Complete:" + sceneName + "/GameMode:"+ GetGameModeType() + "/" + Time.time );

        Init();
	}

	IEnumerator UnLoadLoadedStadium()
	{
		if (string.IsNullOrEmpty( _lastLoadedStadium ))
			yield break;

		bool bUnloded = SceneManager.UnloadScene( _lastLoadedStadium );
		//bool bUnloded = Application.UnloadLevel( _lastLoadedStadium );
		while (false == bUnloded)
			yield return null;

		Resources.UnloadUnusedAssets();
	}

	GameObject _mapCam;

	void PreInit()
	{
		_mapWorksObj = GameObject.Find( "Works_OBJ" );
		_mapAssetRoot = GameObject.Find( "AssetRoot" ); // need same root name or special component

		if (null == _mapAssetRoot)
		{
			_mapAssetRoot = GameObject.FindGameObjectWithTag( "LevelAssetRoot" );
		}

		if (null == _mapAssetRoot)
		{
			Debug.LogError( "@ERROR/(null == _mapAssetRoot)" + Time.realtimeSinceStartup );
		}
		else
		{
			switch (_curGameMode.GetModeType())
			{
			case PlayDataManager.GameMode.NormalGame:
			case PlayDataManager.GameMode.NormalGame111:
			case PlayDataManager.GameMode.PitchingChallenge:
			case PlayDataManager.GameMode.SwitchNormalPitching:
				{
					Transform tmBSOCountBoard = _mapAssetRoot.transform.Find( "Environment/Common_ballcount_Ani" );
					GameObject bsoCountBoard = null;
					if (null == tmBSOCountBoard)
					{
						Debug.LogError( "@ERROR/(null == tmBSOCountBoard)/'Environment/Common_ballcount_Ani'/" + Time.realtimeSinceStartup );
					}
					else
					{
						//@ 경로 계층 상에 모든 오브젝트들이 완벽하게 있는 경우만 경기장 카운터 세팅을 한다.
						bsoCountBoard = tmBSOCountBoard.gameObject;
						PlayDataManager.instance.SetEventStadiumBoards( bsoCountBoard );
					}
				}
				break;
			}
		}

		// will adding tag to asset root

		if (_mapWorksObj)
		{
			_mapCam = _mapWorksObj.transform.FindChild( "Works_Cam" ).gameObject;

			if (_mapCam != null)
			{
				Camera[] cameras = SZCameraManager.Instance.gameObject.GetComponentsInChildren<Camera>( true );

				foreach (Camera cam in cameras)
				{
					Utils.CopyCameraComponentValues( cam.gameObject, _mapCam );
				}

				// cam effect modify
				//Utils.CopyCameraComponentValues( SZCameraEffect.Instance.transform.FindChild( "Camera" ).gameObject, _mapCam );
			}

			_mapWorksObj.SetActive( false );
		}
	}

	void Init()
	{
        Debug.Log( "+++ SZGameManager.Init()" );

		if (null == _ballBatting)
		{
			_ballBatting = Utils.Instantiate( "ball/BallForBattring", "ballBatting");
		}

		BattedBallDataManager.Instance.InitBall( _ballBatting );

		if (null == _curGameMode)
			return;

		if (_bInitialized)
		{
			PlayDataManager.Instance.ClearPlayerData();
		}
		else
		{
			PlayDataManager.Instance.LoadFielderPositionData();
			//PlayDataManager.Instance.SetStadiumName();
			PlayDataManager.Instance.InitPositionData();

			PhysicHelper.Instance.InitPhysicHelper();
		}

        PhysicHelper.Instance.ResultDelegate += _curGameMode.SimulationResult;

		//this.gameObject.GetComponent<CharacterLoader>().LoadCharacter();           

		GameEventDelayTimer.SetTimeDelayOn(false == SZGameDataManager.Instance.IsContinueGame);

		_curGameMode.Init();

        if (false == _bInitialized)
            SZGameManager.Instance.ChangeGameState( PlayDataManager.GamePlayState.Initialize );
        else
            ShowBreakTimeMovie();

		_bInitialized = true;
	}
    
    public void LeaveGame()
    {
		ResetCollecterTime();

        StartWarmUpMode();
    }

    public void ResetCollecterTime()
    {
		// 콜랙터 구동시간을 5분으로 변경 by kkandolee
		_collectorWaitTime = 300.0f;
#if !UNITY_EDITOR && USE_SENSOR || USE_MOCKSENSOR
		Sensor.Instance.SetCollectorSpeed( 1 );
#endif
    }


	public PlayDataManager.GamePlayState GetCurGameState()
	{
		return _curState;
	}


	public void ChangeGameState( PlayDataManager.GamePlayState nextState )
	{
		// when state is changing, ignore new state
		if (_nextState != _curState)
		{
			Debug.LogWarningFormat("@WARNING/ChangeGameState. _curState : {0}, _nextState : {1}, nextState : {2}", _curState, _nextState, nextState );
			return;
		}

        if (_nextState == nextState)
        {
            Debug.LogWarning("@Warning/ChangeGameState/nextState("+ nextState + "/"+ _nextState + ")/Call overlapped same event.");
        }
		
		Debug.Log("++++++++++++ ChangeGameState " + nextState.ToString());

		_nextState = nextState;

        if(PlayDataManager.GamePlayState.GameReadyForPitching == nextState && _curState != nextState)
        {
            Capturer.NasmoResultWithPlay(SZGameManager.Instance.GetGameModeType());
        }
	}

	public void ResetProcessing()
	{
		GameEventDelayTimer.ResetProcessing();
	}
    void UpdateCurGameState()
    {   
        // 1. ignore state change, when cut-scnee playing, 2. when requested delay events for GameStarted, GameEntry.
		if (_curState != _nextState 
            && false == IsPlayingCutScene() 
            && false == GameEventDelayTimer.IsProcessing
            && false == Capturer.IsPlayingTimeEpsilonOn )
		{
            ProcessEnterGameState( _nextState );
            ProcessExitGameState( _curState );
			_curState = _nextState;

            SZActionManager.ProcessAllMgrConditionAction();

			SystemMonitorManager.Instance.SetGameCurrentState( _curState );

            GameEventDelayTimer.ChangeEvent( _curState );
        }
    }


	void ProcessEnterGameState( PlayDataManager.GamePlayState gameState )
	{
		switch ( gameState )
		{
            case PlayDataManager.GamePlayState.Initialize:
                SystemMonitorManager.Instance.SetGamePlayState( gameState );
                break;

            case PlayDataManager.GamePlayState.BreakTime:
                SZUIManager.Instance.SetVisibleUI<LoadingUI>( false );
                SZUIManager.Instance.SetVisibleUI<SteelCutUI>( false );
                StartCoroutine( ProcessReservedGameMode() );
                break;

            case PlayDataManager.GamePlayState.Sleep:
                {
					Debug.Log( "Screen Start SleepMode" );
                    PlayDataManager.Instance.ClearAllTeamData();
                    PlayDataManager.Instance.ClearPlayerData();
                    DestroyGameMode();
                    SZUIManager.Instance.SetVisibleUI<SteelCutUI>( true );
                    MoviePlayer.Instance.SetVisible( false );
                }                
                break;

			case PlayDataManager.GamePlayState.GameLoading:
                {
                    MoviePlayer.Instance.SetVisible( false );
					if (_curGameMode.GetModeType() != PlayDataManager.GameMode.WarmUp)
					{
						ShowAppInfo();
					}
					else
					{
						SZUIManager.Instance.SetVisibleUI<LoadingUI>( true );
						SZUIManager.Instance.SetVisibleUI<SteelCutUI>( false );
						SZUIManager.Instance.SetVisibleUI<AppInfoUI>( false );
						StartCoroutine( LoadStadium() );			
					}
                }
                break;

			case PlayDataManager.GamePlayState.GameReady:
				break;

			case PlayDataManager.GamePlayState.GameEntry:
                MoviePlayer.Instance.SetVisible( false );
                SZUIManager.Instance.SetVisibleUI<LoadingUI>( false );
				GameEventDispatcher.Instance.RaiseEvent( EventId.GameEntry );

				_collectorWaitTime = 300.0f;

#if !UNITY_EDITOR && USE_SENSOR || USE_MOCKSENSOR
				if ( GetGameModeType() != PlayDataManager.GameMode.WarmUp )
				{			
					Sensor.Instance.SetCollectorSpeed( 9 );
				}
#endif
				break;

			case PlayDataManager.GamePlayState.GameStart:
				break;

			case PlayDataManager.GamePlayState.GameReadyForPlay:
				GameEventDispatcher.Instance.RaiseEvent( EventId.GameStarted );
#if !UNITY_EDITOR && USE_SENSOR || USE_MOCKSENSOR
				if ( GetGameModeType() == PlayDataManager.GameMode.WarmUp )
				{			
					Sensor.Instance.SetCollectorSpeed( 9 );
				}
#endif
				break;

			case PlayDataManager.GamePlayState.GameReadyForPitching:
#if USE_FAST_CAM
				// 타임스케일 조절
				Time.timeScale = 1.0f;
#endif
                BaseGameMode.ResetTurnByTurn();
				if(SZGameDataManager.instance.IsContinueGame && _isResetBatterCount == false)
				{
					BaseGameMode.ResetCountBatterReadyOnBatterInit();
					_isResetBatterCount = true;
				}
				

                _collectorWaitTime = 300.0f;
                SZUIManager.Instance.SetVisibleUI<LoadingUI>(false);

                if (true == Configuration.Instance.Data.IsFrameVariantTest)
                {
                    FrameTargetHelper.SetFrameTarget(Configuration.Instance.Data.frameRateTarget);
                }

                GameEventDispatcher.Instance.RaiseEvent( EventId.GameReadyForDefense );

                break;

			case PlayDataManager.GamePlayState.GamePlayingForBatting:
#if !UNITY_EDITOR && USE_SENSOR || USE_MOCKSENSOR
				// 무조건 콜랙터를 돌게 한다.
				Sensor.Instance.SetCollectorSpeed( 9 );
#endif
				GameEventDispatcher.Instance.RaiseEvent( EventId.GameBatterReady );
				break;

			case PlayDataManager.GamePlayState.ThreeOutChange:
				{
					//@ 투구 모드는 공수 전환이 일어나지 않으므로 패스.
					if (GetGameModeType() != PlayDataManager.GameMode.PitchingChallenge)
					{
						GameEventDispatcher.Instance.RaiseEvent(EventId.GameChangeSide);
					}
				}
				break;

			case PlayDataManager.GamePlayState.GameEnd:
				GameEventDispatcher.Instance.RaiseEvent( EventId.GameEnd );
				break;
		}
	}

	void ShowAppInfo()
	{
		int appinfoindex = 0;
		SZUIManager.Instance.SetVisibleUI<AppInfoUI>( true );

		StartCoroutine( ShowAppInfoArray( appinfoindex ) );
	}

	IEnumerator ShowAppInfoArray(int appinfoindex)
	{
		UISystemMonitor sysmon = SystemMonitorManager.Instance.GetSysmon();
		AppInfoUI appinfoui = SZUIManager.Instance.GetUIComponent<AppInfoUI>();

		_ShowAppInfoTime = Configuration.Instance.Data.ShowAppInfoTime;
		_WaitAppInfoTime = Configuration.Instance.Data.WaitAppInfoTime;

		if (sysmon == null || appinfoui == null)
		{
			Debug.LogError( "sysmon or app_info_ui is null!" );
			SZUIManager.Instance.SetVisibleUI<LoadingUI>( true );
			SZUIManager.Instance.SetVisibleUI<SteelCutUI>( false );
			SZUIManager.Instance.SetVisibleUI<AppInfoUI>( false );
			StartCoroutine( LoadStadium() );
			StopCoroutine( "ShowAppInfoArray" );
		}

		if(appinfoindex == 0)
		{
			sysmon.AppInfoWindow.SetActive( true );
			sysmon.AppInfoBackground.gameObject.SetActive( true );
			appinfoui.Background.gameObject.SetActive( true );
			sysmon.AppInfoTexture[appinfoindex].gameObject.SetActive( true );
			appinfoui.AppinfoTexture[appinfoindex].gameObject.SetActive( true );
			TweenAlpha.Begin( sysmon.AppInfoTexture[appinfoindex].gameObject, _ShowAppInfoTime / sysmon.AppInfoTexture.Length / 2, 1.0f );
			TweenAlpha.Begin( appinfoui.AppinfoTexture[appinfoindex].gameObject, _ShowAppInfoTime / sysmon.AppInfoTexture.Length / 2, 1.0f );
			yield return new WaitForSeconds( _ShowAppInfoTime / sysmon.AppInfoTexture.Length / 2 );
			yield return new WaitForSeconds( _WaitAppInfoTime / sysmon.AppInfoTexture.Length );

			MessageTable messageTable = BSDataManager.Instance.Get().GetMessageTable();
			string audioName = messageTable.Find( "STR_SwichUser_TTS" ).message;

			AudioManager.Instance._AudioMixer.SetFloat( "TTSVol", 30.0f );
			AudioManager.Instance._AudioMixer.SetFloat( "TTSRoomLF", -10000.0f );
			AudioManager.Instance.PlaySound( audioName, AudioManager.SoundType.TTS );
		}
		
		TweenAlpha.Begin( sysmon.AppInfoTexture[appinfoindex].gameObject, _ShowAppInfoTime / sysmon.AppInfoTexture.Length / 2, 0.0f );
		TweenAlpha.Begin( appinfoui.AppinfoTexture[appinfoindex].gameObject, _ShowAppInfoTime / sysmon.AppInfoTexture.Length / 2, 0.0f );
		if (appinfoindex + 1 <= sysmon.AppInfoTexture.Length - 1)
		{
			sysmon.AppInfoTexture[appinfoindex + 1].gameObject.SetActive( true );
			appinfoui.AppinfoTexture[appinfoindex + 1].gameObject.SetActive( true );
			TweenAlpha.Begin( sysmon.AppInfoTexture[appinfoindex + 1].gameObject, _ShowAppInfoTime / sysmon.AppInfoTexture.Length / 2, 1.0f );
			TweenAlpha.Begin( appinfoui.AppinfoTexture[appinfoindex + 1].gameObject, _ShowAppInfoTime / sysmon.AppInfoTexture.Length / 2, 1.0f );
			yield return new WaitForSeconds( _WaitAppInfoTime / sysmon.AppInfoTexture.Length );
		}
		yield return new WaitForSeconds( _ShowAppInfoTime / sysmon.AppInfoTexture.Length / 2 );
		

		if (appinfoindex >= sysmon.AppInfoTexture.Length - 1)
		{
			for(int i = 0; i < sysmon.AppInfoTexture.Length; i++)
			{
				sysmon.AppInfoTexture[i].gameObject.SetActive( false );
				appinfoui.AppinfoTexture[i].gameObject.SetActive( false );
			}
			sysmon.AppInfoWindow.SetActive( false );
			sysmon.AppInfoBackground.gameObject.SetActive( false );
			appinfoui.Background.gameObject.SetActive( false );
			appinfoindex = 0;
			
			float ttsvol = -0.5f;
			float ttsroomlf = -800.0f;
			AudioManager.Instance._AudioMixer.SetFloat( "TTSVol", ttsvol );
			AudioManager.Instance._AudioMixer.SetFloat( "TTSRoomLF", ttsroomlf );
			SZUIManager.Instance.SetVisibleUI<LoadingUI>( true );
			SZUIManager.Instance.SetVisibleUI<SteelCutUI>( false );
			SZUIManager.Instance.SetVisibleUI<AppInfoUI>( false );
			StartCoroutine( LoadStadium() );
		}
		else
		{
			appinfoindex++;
			StartCoroutine( ShowAppInfoArray( appinfoindex ) );
		}
	}

    IEnumerator ProcessReservedGameMode()
    {
        if (PlayDataManager.GameMode.None != _reservedMode && false == string.IsNullOrEmpty( _reservedMapName ))
        {
            yield return new WaitForEndOfFrame();

            InternalStartGameMode( _reservedMode, _reservedMapName );

            _reservedMode = PlayDataManager.GameMode.None;
            _reservedMapName = null;
        }
    }


	void ProcessExitGameState( PlayDataManager.GamePlayState gameState )
	{
		switch ( gameState )
		{
            case PlayDataManager.GamePlayState.Initialize:
                break;

            case PlayDataManager.GamePlayState.Sleep:                      
                break;

            case PlayDataManager.GamePlayState.BreakTime:
                break;

            case PlayDataManager.GamePlayState.GameLoading:
                break;

			case PlayDataManager.GamePlayState.GameReady:
				break;

			case PlayDataManager.GamePlayState.GameEntry:                
				break;

			case PlayDataManager.GamePlayState.GameStart:
				break;

			case PlayDataManager.GamePlayState.GameReadyForPlay:                
				break;

			case PlayDataManager.GamePlayState.GameReadyForPitching:
				break;

			case PlayDataManager.GamePlayState.GamePlayingForBatting:
				break;

			case PlayDataManager.GamePlayState.ThreeOutChange:
				break;

			case PlayDataManager.GamePlayState.GameEnd:
				break;
		}
	}

	void Update()
	{
		if ( _curState < PlayDataManager.GamePlayState.GameLoading || _curState == PlayDataManager.GamePlayState.GameEnd )
		{
			if ( _collectorWaitTime > 0 )
			{
				_collectorWaitTime -= Time.deltaTime;

				if ( _collectorWaitTime < 0 )
				{
					_collectorWaitTime = 0;

#if !UNITY_EDITOR && USE_SENSOR || USE_MOCKSENSOR
					Sensor.Instance.SetCollectorSpeed( 0 );
#endif
				}
			}
		}
		else if ( null != _curGameMode && _curGameMode.GetModeType() == PlayDataManager.GameMode.WarmUp && _curState < PlayDataManager.GamePlayState.GameReadyForPitching )
		{
			if ( _collectorWaitTime > 0 )
			{
				_collectorWaitTime -= Time.deltaTime;

				if ( _collectorWaitTime < 0 )
				{
					_collectorWaitTime = 0;

#if !UNITY_EDITOR && USE_SENSOR || USE_MOCKSENSOR
					Sensor.Instance.SetCollectorSpeed( 0 );
#endif
				}
			}
		}

        // cur game state change to next game state
        UpdateCurGameState();

		if (null == _curGameMode)
			return;

		_curGameMode.UpdateMode();

        BaseGameMode.TimerForRollback.Update();
    }

	void FixedUpdate()
	{
		if ( null == _curGameMode )
			return;

		_curGameMode.FixedUpdateMode();
	}

	public void ProcessGameEvent( EventId evtId )
	{
		if ( null == _curGameMode )
			return;

		_curGameMode.ProcessGameEvent( evtId );
	}

    public void IncreaseWarmUpModePlayCount()
    {
        _warmUpModePlayCnt++;
    }

    public int GetWarmUpModePlayCount()
    {
        return _warmUpModePlayCnt;
    }

    public void ResetWarmUpModePlayCount()
    {
        _warmUpModePlayCnt = 0;
    }

	//#TODO: 추후 조건 보강 필요.
	public bool IsCanRollback
	{
		get
		{
			//return PlayDataManager.Instance.IsCanRollback 
			//	&& GetCurGameState() == PlayDataManager.GamePlayState.GameReadyForPitching;
			return PlayDataManager.Instance.IsCanRollback;
		}
	}

	public void RollbackToLastGameState()
	{
        PlayDataManager.GameMode gameMode = _curGameMode.GetModeType();

		if ( true == PlayDataManager.instance.IsCanRollback 
			&& (gameMode == PlayDataManager.GameMode.NormalGame || gameMode == PlayDataManager.GameMode.NormalGame111) )
		{
			BaseNormalGameMode normalGameMode = SZGameManager.Instance.GetGameMode() as BaseNormalGameMode;
			if (null == normalGameMode)
			{
				Debug.LogError( "@ERROR/(null == normalGameMode)/RollbackToLastGameState/" + Time.realtimeSinceStartup );
			}
			else
			{
				Debug.Log( "RollbackToLastGameState()/gameMode("+ gameMode + ")/" );
				normalGameMode.RollbackGameToPreviousTurn();
			}
		}
	}

    public void ShowBreakTimeMovie()
    {
        if (null == _curGameMode)
            return;

        if (_curGameMode.GetModeType() != PlayDataManager.GameMode.WarmUp)
            return;

        if (_curState != PlayDataManager.GamePlayState.GameLoading &&
            _curState != PlayDataManager.GamePlayState.Initialize)
            return;        

        WarmUpMode warmupMode = _curGameMode as WarmUpMode;
        if (null != warmupMode)
            warmupMode.ShowBreakTimeMovie();
    }

    bool _bPlayingCutScene = false;
    public void SetPlayingCutScene( bool bPlsying )
    {
		Utils.DebugLog( "------------------ SetPlayingCutScene ", bPlsying );
        _bPlayingCutScene = bPlsying;
    }

    public bool IsPlayingCutScene()
    {
        return _bPlayingCutScene;
    }

	//@ 현재 게임 상태가 준비 모드 인지 여부.
    public bool IsCurStateAsReady
    {
        get
        {
            return PlayDataManager.GamePlayState.GameReadyForPitching == _curState
                      || PlayDataManager.GamePlayState.GameReadyForPlay == _curState;
        }
    }

    public bool CanPlayWarmUpMode()
    {
        // mini mode can't play warmp up mode
        if (SZGameDataManager.Instance.IsMiniMode())
            return false;

        BaseGameMode gameMode = GetGameMode();
        if (null == gameMode)
            return false;

        if (gameMode.GetModeType() != PlayDataManager.GameMode.WarmUp)
            return false;

        WarmUpMode warmUpMode = (WarmUpMode)gameMode;
        if (false == warmUpMode.IsStop())
            return false;

        // user can play warm up modeo once
        // after nm, bc, tr mode is played, user can play warm up mode again
        if (0 == GetWarmUpModePlayCount() &&
            gameMode.GetModeType() == PlayDataManager.GameMode.WarmUp)
            return true;        

        return false;
    }

    public bool IsPlayingWarmUpMode()
    {
        BaseGameMode baseMode = SZGameManager.Instance.GetGameMode();
        if ( baseMode.GetModeType() == PlayDataManager.GameMode.WarmUp )
        {
            WarmUpMode warmupMode = (WarmUpMode)baseMode;
            if ( false == warmupMode.IsStop() )
                return true;
        }

        return false;
    }

	private bool ValidationCheckPlayerStatus( List<UserData> userList, PlayerStatus playerStatus )
	{
		BaseGameMode gameMode = GetGameMode();

		int userCnt = userList.Count;

		for ( int i = 0; i < userCnt; ++i )
		{
			UserData userData = userList[i];

			PlayerStatus status = gameMode.GetPlayerStatus( userData.uid );
			if ( status != playerStatus )
			{
				return false;
			}
		}

		return true;
	}

	public void SetAdminPauseValue( bool isPause )
	{
		_adminGamePause = isPause;

		_curGameMode.SetGamePauseEvent( _adminGamePause );
	}

	public bool GetAdminPauseValue()
	{
		return _adminGamePause;
	}
}

